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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gemstone.PQDIF.Logical;
using Gemstone.PQDIF.Physical;

namespace PQDIFExplorer.Web
{
    public class PQDIFFile
    {
        public string Key => GetKey();
        public string Name { get; }
        public IEnumerable<Record> Records { get; }

        public PQDIFFile(string name, IEnumerable<Record> records)
        {
            Name = name;
            Records = records;
        }

        public static async Task<IEnumerable<Record>> ParseAsync(byte[] fileData, CancellationToken cancellationToken = default)
        {
            List<Record> records = new List<Record>();
            using MemoryStream stream = new MemoryStream(fileData);
            using PhysicalParser parser = new PhysicalParser();
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

                records.Add(record);
            }

            return records;
        }

        private string GetKey()
        {
            Encoding utf8 = new UTF8Encoding(false);
            byte[] nameData = utf8.GetBytes(Name);
            return Convert.ToBase64String(nameData);
        }
    }

    public class PQDIFFileCache
    {
        // Everything is accessed from the UI thread so no need for ConcurrentDictionary
        private Dictionary<string, PQDIFFile> Lookup { get; } = new Dictionary<string, PQDIFFile>();

        // Allow components to react to cache updates
        public event EventHandler? Updated;

        public IEnumerable<PQDIFFile> RetrieveAll() =>
            Lookup.Values.OrderBy(file => file.Name);

        public PQDIFFile? Retrieve(string fileKey)
        {
            if (Lookup.TryGetValue(fileKey, out PQDIFFile file))
                return file;

            return null;
        }

        public PQDIFFile Save(string fileName, IEnumerable<Record> records)
        {
            PQDIFFile file = TryCache(fileName, records)
                ?? HandleCacheCollision(fileName, records);

            OnCacheUpdated();
            return file;
        }

        public bool Remove(string fileKey)
        {
            bool removed = Lookup.Remove(fileKey);
            OnCacheUpdated();
            return removed;
        }

        private PQDIFFile HandleCacheCollision(string originalFileName, IEnumerable<Record> records)
        {
            int num = 2;
            string rootFileName = Path.GetFileNameWithoutExtension(originalFileName);
            string extension = Path.GetExtension(originalFileName);
            string ResolveFileName() => $"{rootFileName} ({num}){extension}";

            while (true)
            {
                string fileName = ResolveFileName();
                PQDIFFile? file = TryCache(fileName, records);

                if (file != null)
                    return file;

                num++;
            }
        }

        private PQDIFFile? TryCache(string fileName, IEnumerable<Record> records)
        {
            PQDIFFile file = new PQDIFFile(fileName, records);
            return Lookup.TryAdd(file.Key, file) ? file : null;
        }

        private void OnCacheUpdated() =>
            Updated?.Invoke(this, EventArgs.Empty);
    }
}
