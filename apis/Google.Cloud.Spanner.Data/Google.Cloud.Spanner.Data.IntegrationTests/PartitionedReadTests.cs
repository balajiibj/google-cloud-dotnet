﻿// Copyright 2017 Google Inc. All Rights Reserved.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#region

using System;
using System.Collections;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Api.Gax;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Spanner.V1;
using Grpc.Auth;
using Xunit;
using Xunit.Abstractions;

#endregion

// ReSharper disable PossibleNullReferenceException

namespace Google.Cloud.Spanner.Data.IntegrationTests
{
    [Collection(nameof(TestDatabaseFixture))]
    public class PartitionedReadTests
    {
        // ReSharper disable once UnusedParameter.Local
        public PartitionedReadTests(TestDatabaseFixture testFixture, ITestOutputHelper outputHelper)
        {
            _testFixture = testFixture;
#if LoggingOn
            SpannerConnection.ConnectionPoolOptions.LogLevel = LogLevel.Debug;
            SpannerConnection.ConnectionPoolOptions.LogPerformanceTraces = true;
#endif
            TestLogger.TestOutputHelper = outputHelper;
        }

        private readonly TestDatabaseFixture _testFixture;
        private int _rowsRead = 0;

        [Fact]
        public async Task DistributedReadAsync()
        {
            int numRows;
            using (var connection = await _testFixture.GetTestDatabaseConnectionAsync())
            using (var cmd = connection.CreateSelectCommand("SELECT COUNT(*) FROM Orders"))
            {
                numRows = await cmd.ExecuteScalarAsync<int>();
            }

            using (var connection = new SpannerConnection(_testFixture.ConnectionString))
            {
                await connection.OpenAsync();

                using (var transaction = await connection.BeginReadOnlyTransactionAsync())
                using (var cmd = connection.CreateSelectCommand("SELECT * FROM Orders"))
                {
                    transaction.DisposeBehavior = DisposeBehavior.CloseResources;
                    cmd.Transaction = transaction;
                    var partitions = await cmd.GetReaderPartitionsAsync(1000);
                    var transactionId = transaction.TransactionId;

                    //we simulate a serialization/deserialization step in the call to the subtask.
                    await Task.WhenAll(partitions.Select(
                            x => DistributedReadWorkerAsync(CommandPartition.FromBase64String(x.ToBase64String()),
                                            TransactionId.FromBase64String(transactionId.ToBase64String()))))
                        .ConfigureAwait(false);
                }

                Assert.Equal(numRows, _rowsRead);
            }
        }

        /// <summary>
        /// TransactionId and CommandPartition are serializable.
        /// </summary>
        private async Task DistributedReadWorkerAsync(CommandPartition readPartition, TransactionId id)
        {
            //note: we only use state provided by the arguments here.
            using (var connection = new SpannerConnection(id.ConnectionString))
            using (var transaction = connection.BeginReadOnlyTransaction(id))
            {
                using (var cmd = connection.CreateCommandWithPartition(readPartition, transaction))
                {
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync())
                        {
                            Interlocked.Increment(ref _rowsRead);
                        }
                    }
                }
            }
        }
    }
}
