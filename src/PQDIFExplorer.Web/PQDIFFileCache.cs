//******************************************************************************************************
//  PQDIFFileCache.cs - Gbtc
//
//  Copyright © 2024, Grid Protection Alliance.  All Rights Reserved.
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

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Gemstone.PQDIF.Logical;
using Gemstone.PQDIF.Physical;
using Gemstone.Web.Razor.ServiceWorkers;
using Microsoft.JSInterop;

namespace PQDIFExplorer.Web;

public class PQDIFKeyData
{
    public string Key { get; set; }
    public string Name { get; set; }
    public bool HasUnsavedChanges { get; set; }

    public PQDIFKeyData()
    {
        Key = string.Empty;
        Name = string.Empty;
    }

    public PQDIFKeyData(string key)
    {
        Key = key;

        Encoding encoding = new UTF8Encoding(false);
        byte[] keyBytes = Convert.FromBase64String(key);
        string json = encoding.GetString(keyBytes);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement nameProperty = document.RootElement.GetProperty("name");
        Name = nameProperty.GetString() ?? string.Empty;
    }

    public PQDIFKeyData(string key, string name)
    {
        Key = key;
        Name = name;
    }
}

public class PQDIFFile(string key, string name, IEnumerable<Record> records, Action onUpdate)
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public IEnumerable<Record> Records { get; } = records;

    public bool HasUnsavedChanges
    {
        get => _HasUnsavedChanges;
        set
        {
            _HasUnsavedChanges = value;
            OnUpdate?.Invoke();
        }
    }

    private Action OnUpdate { get; } = onUpdate;
    private bool _HasUnsavedChanges { get; set; }

    public static async IAsyncEnumerable<Record> ParseAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using Stream? localStream = !stream.CanSeek
            ? new MemoryStream()
            : null;

        Stream parsingStream = localStream ?? stream;
        await using PhysicalParser parser = new();
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

public class PQDIFFileCache(HttpClient httpClient, IServiceWorkerContainer serviceWorkerContainer, IJSRuntime jsRuntime)
{
    private HttpClient HttpClient { get; } = httpClient;
    private IServiceWorkerContainer ServiceWorkerContainer { get; } = serviceWorkerContainer;
    private IJSRuntime JSRuntime { get; } = jsRuntime;

    // Client-side Blazor is single-threaded, so no need for ConcurrentDictionary
    private Dictionary<string, PQDIFFile> Lookup { get; } = [];

    // Allow components to react to cache updates
    public event EventHandler? Updated;

    public async IAsyncEnumerable<PQDIFKeyData> RetrieveKeysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ServiceWorkerContainer.WhenReady;

        using HttpResponseMessage response = await HttpClient.GetAsync("/PQDIF/List", cancellationToken);

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new Exception($"Invalid response listing PQDIF files: {response.ReasonPhrase} ({response.StatusCode})");

        if (response.Content.Headers.ContentLength == 0)
            yield break;

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, default, cancellationToken);

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            JsonElement keyProperty = element.GetProperty("key");
            string key = keyProperty.GetString() ?? string.Empty;

            JsonElement nameProperty = element.GetProperty("name");
            string name = nameProperty.GetString() ?? string.Empty;

            PQDIFKeyData keyData = new(key, name);

            if (Lookup.TryGetValue(key, out PQDIFFile? file))
                keyData.HasUnsavedChanges = file.HasUnsavedChanges;

            yield return keyData;
        }
    }

    public async Task<PQDIFFile?> RetrieveAsync(string fileKey, CancellationToken cancellationToken = default)
    {
        await ServiceWorkerContainer.WhenReady;

        if (Lookup.TryGetValue(fileKey, out PQDIFFile? cachedFile))
            return cachedFile;

        string url = $"/PQDIF/Retrieve/{fileKey}";
        using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (response.StatusCode != System.Net.HttpStatusCode.OK)
            throw new Exception($"Invalid response retrieving PQDIF file: {response.ReasonPhrase} ({response.StatusCode})");

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        IEnumerable<Record> records = await PQDIFFile
            .ParseAsync(stream, cancellationToken)
            .ToListAsync(cancellationToken);

        PQDIFKeyData keyData = new(fileKey);
        PQDIFFile file = new(fileKey, keyData.Name, records, OnCacheUpdated);
        Lookup[fileKey] = file;
        return file;
    }

    public async Task<PQDIFKeyData[]> SaveAsync(object fileSource, CancellationToken cancellationToken = default)
    {
        await ServiceWorkerContainer.WhenReady;
        const string CacheFunction = "pqdif.cacheFilesAsync";
        PQDIFKeyData[] fileKeyData = await JSRuntime.InvokeAsync<PQDIFKeyData[]>(CacheFunction, cancellationToken, fileSource);
        OnCacheUpdated();
        return fileKeyData;
    }

    public async Task PurgeAsync(string fileKey, CancellationToken cancellationToken = default)
    {
        await ServiceWorkerContainer.WhenReady;

        Lookup.Remove(fileKey);

        using HttpRequestMessage request = new(HttpMethod.Delete, "/PQDIF/Purge");
        request.Content = new StringContent(fileKey);

        using HttpResponseMessage response = await HttpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.OK)
            OnCacheUpdated();
        else if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new Exception($"Invalid response purging PQDIF file: {response.ReasonPhrase} ({response.StatusCode})");
    }

    public async Task CommitEditsAsync(string fileKey, CancellationToken cancellationToken = default)
    {
        if (!Lookup.TryGetValue(fileKey, out PQDIFFile? file))
            return;

        await ServiceWorkerContainer.WhenReady;
        cancellationToken.ThrowIfCancellationRequested();

        using MemoryStream stream = new();
        using PhysicalWriter writer = new(stream);
        List<Record> records = file.Records.ToList();
        Record lastRecord = records.Last();

        foreach (Record record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isLast = (record == lastRecord);
            await writer.WriteRecordAsync(record, isLast);
        }

        stream.Position = 0;

        PQDIFKeyData keyData = new(fileKey);
        HttpContent keyContent = new StringContent(fileKey);
        HttpContent streamContent = new StreamContent(stream);

        MultipartFormDataContent requestContent = new()
        {
            { keyContent, "pqdifKey" },
            { streamContent, "pqdifFile", keyData.Name }
        };

        await HttpClient.PostAsync("/PQDIF/Cache", requestContent, cancellationToken);

        file.HasUnsavedChanges = false;
        Updated?.Invoke(this, EventArgs.Empty);
    }

    public void FlushParsedData(string fileKey)
    {
        Lookup.Remove(fileKey);
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void OnCacheUpdated() =>
        Updated?.Invoke(this, EventArgs.Empty);
}
