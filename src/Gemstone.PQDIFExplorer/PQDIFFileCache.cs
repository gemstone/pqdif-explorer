//******************************************************************************************************
//  PQDIFFileCache.cs - Gbtc
//
//  Copyright © 2020, Grid Protection Alliance.  All Rights Reserved.
//
//  Licensed to the Grid Protection Alliance (GPA) under one or more contributor license agreements. See
//  the NOTICE file distributed with this work for additional information regarding copyright ownership.
//  The GPA licenses this file to you under the MIT License (MIT), the "License"; you may not use this
//  file except in compliance with the License. You may obtain a copy of the License at:
//
//      http://opensource.org/licenses/MIT
//
//  Unless agreed to in writing, the subject software distributed under the License is distributed on an
//  "AS-IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. Refer to the
//  License for the specific language governing permissions and limitations.
//
//  Code Modification History:
//  ----------------------------------------------------------------------------------------------------
//  07/05/2020 - Stephen C. Wills
//       Generated original version of source code.
//
//******************************************************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gemstone.PQDIF.Logical;
using Gemstone.PQDIF.Physical;
using Microsoft.JSInterop;

namespace PQDIFExplorer.Web
{
    public class FileKeyData
    {
        public string Key { get; set; }
        public string Name { get; set; }

        public FileKeyData()
        {
            Key = string.Empty;
            Name = string.Empty;
        }

        public FileKeyData(string key)
        {
            Key = key;

            Encoding encoding = new UTF8Encoding(false);
            byte[] keyBytes = Convert.FromBase64String(key);
            string json = encoding.GetString(keyBytes);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement nameProperty = document.RootElement.GetProperty("name");
            Name = nameProperty.GetString();
        }

        public FileKeyData(string key, string name)
        {
            Key = key;
            Name = name;
        }
    }

    public class PQDIFFile
    {
        public string Key { get; }
        public string Name { get; }
        public IEnumerable<Record> Records { get; }

        public PQDIFFile(string key, string name, IEnumerable<Record> records)
        {
            Key = key;
            Name = name;
            Records = records;
        }

        public static async IAsyncEnumerable<Record> ParseAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await using Stream? localStream = !stream.CanSeek
                ? new MemoryStream()
                : null;

            Stream parsingStream = localStream ?? stream;
            await using PhysicalParser parser = new PhysicalParser();
            await parser.OpenAsync(stream);

            while (parser.HasNextRecord())
            {
                // WASM is single threaded so it needs a
                // moment to update the UI on each iteration
                await Task.Delay(1, cancellationToken);

                Record record = await parser.GetNextRecordAsync();
                ContainerRecord? containerRecord = ContainerRecord.CreateContainerRecord(record);

                if (containerRecord != null)
                {
                    parser.CompressionAlgorithm = containerRecord.CompressionAlgorithm;
                    parser.CompressionStyle = containerRecord.CompressionStyle;
                }

                yield return record;
            }
        }
    }

    public class PQDIFFileCache
    {
        private HttpClient HttpClient { get; }
        private IJSRuntime JSRuntime { get; }

        // Client-side Blazor is single-threaded, so no need for ConcurrentDictionary
        private Dictionary<string, PQDIFFile> Lookup { get; }

        // Allow components to react to cache updates
        public event EventHandler? Updated;

        public PQDIFFileCache(HttpClient httpClient, IJSRuntime jsRuntime)
        {
            HttpClient = httpClient;
            JSRuntime = jsRuntime;
            Lookup = new Dictionary<string, PQDIFFile>();
        }

        public async IAsyncEnumerable<FileKeyData> RetrieveKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await WhenWorkerIsRegistered();

            using HttpResponseMessage response = await HttpClient.GetAsync("/PQDIF/List", cancellationToken);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new Exception($"Invalid response listing PQDIF files: {response.ReasonPhrase} ({response.StatusCode})");

            if (response.Content.Headers.ContentLength == 0)
                yield break;

            await using Stream responseStream = await response.Content.ReadAsStreamAsync();
            using JsonDocument document = await JsonDocument.ParseAsync(responseStream, default, cancellationToken);

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                JsonElement keyProperty = element.GetProperty("key");
                string key = keyProperty.GetString();

                JsonElement nameProperty = element.GetProperty("name");
                string name = nameProperty.GetString();

                yield return new FileKeyData(key, name);
            }
        }

        public async Task<PQDIFFile?> RetrieveAsync(string fileKey, CancellationToken cancellationToken = default)
        {
            await WhenWorkerIsRegistered();

            if (Lookup.TryGetValue(fileKey, out PQDIFFile cachedFile))
                return cachedFile;

            string url = $"/PQDIF/Retrieve/{fileKey}";
            using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new Exception($"Invalid response retrieving PQDIF file: {response.ReasonPhrase} ({response.StatusCode})");

            await using Stream stream = await response.Content.ReadAsStreamAsync();

            IEnumerable<Record> records = await PQDIFFile
                .ParseAsync(stream, cancellationToken)
                .ToListAsync();

            FileKeyData keyData = new FileKeyData(fileKey);
            PQDIFFile file = new PQDIFFile(fileKey, keyData.Name, records);
            Lookup[fileKey] = file;
            return file;
        }

        public async Task<FileKeyData[]> SaveAsync(object fileSource, CancellationToken cancellationToken = default)
        {
            await WhenWorkerIsRegistered();
            const string CacheFunction = "pqdif.cacheFilesAsync";
            FileKeyData[] fileKeyData = await JSRuntime.InvokeAsync<FileKeyData[]>(CacheFunction, cancellationToken, fileSource);
            OnCacheUpdated();
            return fileKeyData;
        }

        public async Task PurgeAsync(string fileKey, CancellationToken cancellationToken = default)
        {
            await WhenWorkerIsRegistered();

            Lookup.Remove(fileKey);

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, "/PQDIF/Purge");
            request.Content = new StringContent(fileKey);

            using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.OK)
                OnCacheUpdated();
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new Exception($"Invalid response purging PQDIF file: {response.ReasonPhrase} ({response.StatusCode})");
        }

        private async Task WhenWorkerIsRegistered()
        {
            Task serviceWorkerTask = JSRuntime
                .InvokeVoidAsync("pqdif.workerIsReady")
                .AsTask();

            await Task.WhenAny(Task.Delay(5000), serviceWorkerTask);

            if (!serviceWorkerTask.IsCompleted)
                throw new TaskCanceledException("Timeout waiting for service worker registration");
        }

        private void OnCacheUpdated() =>
            Updated?.Invoke(this, EventArgs.Empty);
    }
}
