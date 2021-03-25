﻿using SqlCollaborative.Azure.DataPipelineTools.DataLake.Model;
using Azure.Storage.Files.DataLake;
using Flurl;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Converters;

namespace SqlCollaborative.Azure.DataPipelineTools.DataLake
{
    public class DataLakeService
    {
        private readonly ILogger _logger;
        private readonly DataLakeFileSystemClient _client;
        internal DataLakeService (ILogger logger, DataLakeFileSystemClient client)
        {
            _logger = logger;
            _client = client;
        }

        public async Task<string> CheckPathAsync(string path, bool isDirectory)
        {
            if (path == null || path.Trim() == "/")
                return null;

            // Check if the path exists with the casing as is...
            var pathExists = isDirectory ?
                                _client.GetDirectoryClient(path).Exists() :
                                _client.GetFileClient(path).Exists();
            if (pathExists)
                return path;

            _logger.LogInformation($"${(isDirectory ? "Directory" : "File")} '${path}' not found, checking paths case using case insensitive compare...");

            // Split the paths so we can test them seperately
            var directoryPath = isDirectory ? path : Path.GetDirectoryName(path).Replace(Path.DirectorySeparatorChar, '/');
            var filename = isDirectory ? null : Path.GetFileName(path);

            // If the directory does not exist, we find it
            string validDirectory = null;
            var tr = _client.GetDirectoryClient(path).ExistsAsync().Result;
            if (!await _client.GetDirectoryClient(path).ExistsAsync())
            {
                var directoryParts = directoryPath.Split('/');
                foreach (var directoryPart in directoryParts)
                {
                    var searchItem = directoryPart;
                    var validPaths = MatchPathItemsCaseInsensitive(validDirectory, searchItem, true);

                    if (validPaths.Count == 0)
                        return null;
                    else if (validPaths.Count > 1)
                        throw new Exception("Multiple paths matched with case insensitive compare.");

                    validDirectory = validPaths[0];
                }
            }

            if (isDirectory)
                return validDirectory;

            // Now check if the file exists using the corrected directory, and if not find a match...
            var testFilePath = $"{validDirectory ?? ""}/{filename}".TrimStart('/');
            if (_client.GetFileClient(testFilePath).Exists())
                return testFilePath;

            var files = MatchPathItemsCaseInsensitive(validDirectory, filename, false);
            if (files.Count > 1)
                throw new Exception("Multiple paths matched with case insensitive compare.");
            return files.FirstOrDefault();
        }

        private IList<string> MatchPathItemsCaseInsensitive(string basePath, string searchItem, bool isDirectory)
        {
            var paths = _client.GetPaths(basePath).ToList();
            return paths.Where(p => p.IsDirectory == isDirectory && Path.GetFileName(p.Name).Equals(searchItem, StringComparison.CurrentCultureIgnoreCase))
                         .Select(p => p.Name)
                         .ToList();

        }

        public async Task<JObject> GetItemsAsync(DataLakeConfig dataLakeConfig, DataLakeGetItemsConfig getItemsConfig)
        {
            var directory = getItemsConfig.IgnoreDirectoryCase ?
                                await CheckPathAsync(getItemsConfig.Directory, true) :
                                getItemsConfig.Directory;

            if (!_client.GetDirectoryClient(directory).Exists())
                throw new DirectoryNotFoundException("Directory '{directory} could not be found'");

            var paths = _client
                .GetPaths(path: directory ?? string.Empty, recursive: getItemsConfig.Recursive)
                .Select(p => new DataLakeItem
                {
                    Name = Path.GetFileName(p.Name),
                    Directory = p.IsDirectory.GetValueOrDefault(false) ?
                                p.Name :
                                Path.GetDirectoryName(p.Name).Replace(Path.DirectorySeparatorChar, '/'),
                    Url = Url.Combine(dataLakeConfig.BaseUrl, p.Name),
                    IsDirectory = p.IsDirectory.GetValueOrDefault(false),
                    ContentLength = p.ContentLength.GetValueOrDefault(0),
                    LastModified = p.LastModified.ToUniversalTime()
                })
                .ToList();

            // 1: Filter the results using dynamic LINQ
            foreach (var filter in getItemsConfig.Filters.Where(f => f.IsValid))
            {
                var dynamicLinqQuery = filter.GetDynamicLinqString();
                string dynamicLinqQueryValue = filter.GetDynamicLinqValue();
                _logger.LogInformation($"Applying filter: paths.AsQueryable().Where(\"{dynamicLinqQuery}\", \"{filter.Value}\").ToList()");
                paths = paths.AsQueryable().Where(dynamicLinqQuery, dynamicLinqQueryValue).ToList();
            }

            // 2: Sort the results
            if (!string.IsNullOrWhiteSpace(getItemsConfig.OrderByColumn))
            {
                paths = paths.AsQueryable()
                             .OrderBy(getItemsConfig.OrderByColumn + (getItemsConfig.OrderByDescending ? " descending" : string.Empty))
                             .ToList();
            }

            // 3: Do a top N if required
            if (getItemsConfig.Limit > 0 && getItemsConfig.Limit < paths.Count)
                paths = paths.Take(getItemsConfig.Limit).ToList();



            // Output the results
            var isEveryFilterValid = getItemsConfig.Filters.All(f => f.IsValid);
            if (!isEveryFilterValid)
                throw new InvalidFilterCriteriaException("Some filters are not valid");

            var formatter = new IsoDateTimeConverter() {DateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"};
            var filesListJson = isEveryFilterValid ?
                                     $"\"fileCount\": {paths.Count}," +
                                     $"\"files\": {JsonConvert.SerializeObject(paths, Formatting.Indented, formatter)}" :
                                     string.Empty;

            var resultJson = $"{{ {(getItemsConfig.IgnoreDirectoryCase && directory != getItemsConfig.Directory ? $"\"correctedFilePath\": \"{directory}\"," : string.Empty)} {filesListJson} }}";

            return JObject.Parse(resultJson);
        }
    }
}