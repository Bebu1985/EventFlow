﻿// The MIT License (MIT)
// 
// Copyright (c) 2015-2018 Rasmus Mikkelsen
// Copyright (c) 2015-2018 eBay Software Foundation
// https://github.com/eventflow/EventFlow
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of
// this software and associated documentation files (the "Software"), to deal in
// the Software without restriction, including without limitation the rights to
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do so,
// subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
// FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventFlow.Aggregates;
using EventFlow.Configuration;
using EventFlow.Core;
using EventFlow.EventStores;
using EventFlow.Extensions;
using EventFlow.Exceptions;
using EventFlow.Logs;

namespace EventFlow.MsSql.EventStores
{
    public class MsSqlEventPersistence : IEventPersistence
    {
        public class EventDataModel : ICommittedDomainEvent
        {
            public long GlobalSequenceNumber { get; set; }
            public Guid BatchId { get; set; }
            public string AggregateId { get; set; }
            public string AggregateName { get; set; }
            public string Data { get; set; }
            public string Metadata { get; set; }
            public int AggregateSequenceNumber { get; set; }
        }

        private readonly ILog _log;
        private readonly IMsSqlConnection _connection;
        private readonly IEventFlowConfiguration _eventFlowConfiguration;

        public bool PreferStreaming => _eventFlowConfiguration.UseEventStreaming;

        public MsSqlEventPersistence(
            ILog log,
            IMsSqlConnection connection,
            IEventFlowConfiguration eventFlowConfiguration)
        {
            _log = log;
            _connection = connection;
            _eventFlowConfiguration = eventFlowConfiguration;
        }

        public async Task<AllCommittedEventsPage> LoadAllCommittedEvents(
            GlobalPosition globalPosition,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var startPosition = globalPosition.IsStart
                ? 0
                : long.Parse(globalPosition.Value);
            var endPosition = startPosition + pageSize;

            const string sql = @"
                SELECT
                    GlobalSequenceNumber, BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber
                FROM EventFlow
                WHERE
                    GlobalSequenceNumber >= @FromId AND GlobalSequenceNumber <= @ToId
                ORDER BY
                    GlobalSequenceNumber ASC";
            var eventDataModels = await _connection.QueryAsync<EventDataModel>(
                Label.Named("mssql-fetch-events"),
                cancellationToken,
                sql,
                new
                    {
                        FromId = startPosition,
                        ToId = endPosition,
                    })
                .ConfigureAwait(false);

            var nextPosition = eventDataModels.Any()
                ? eventDataModels.Max(e => e.GlobalSequenceNumber) + 1
                : startPosition;

            return new AllCommittedEventsPage(new GlobalPosition(nextPosition.ToString()), eventDataModels);
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> CommitEventsAsync(
            IIdentity id,
            IReadOnlyCollection<SerializedEvent> serializedEvents,
            CancellationToken cancellationToken)
        {
            if (!serializedEvents.Any())
            {
                return new ICommittedDomainEvent[] {};
            }

            var eventDataModels = serializedEvents
                .Select((e, i) => new EventDataModel
                    {
                        AggregateId = id.Value,
                        AggregateName = e.Metadata[MetadataKeys.AggregateName],
                        BatchId = Guid.Parse(e.Metadata[MetadataKeys.BatchId]),
                        Data = e.SerializedData,
                        Metadata = e.SerializedMetadata,
                        AggregateSequenceNumber = e.AggregateSequenceNumber,
                    })
                .ToList();

            _log.Verbose(
                "Committing {0} events to MSSQL event store for entity with ID '{1}'",
                eventDataModels.Count,
                id);

            const string sql = @"
                INSERT INTO
                    EventFlow
                        (BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber)
                        OUTPUT CAST(INSERTED.GlobalSequenceNumber as bigint)
                    SELECT
                        BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber
                    FROM
                        @rows
                    ORDER BY AggregateSequenceNumber ASC";

            IReadOnlyCollection<long> ids;
            try
            {
                ids = await _connection.InsertMultipleAsync<long, EventDataModel>(
                    Label.Named("mssql-insert-events"), 
                    cancellationToken,
                    sql,
                    eventDataModels)
                    .ConfigureAwait(false);
            }
            catch (SqlException exception)
            {
                if (exception.Number == 2601)
                {
                    _log.Verbose(
                        "MSSQL event insert detected an optimistic concurrency exception for entity with ID '{0}'",
                        id);
                    throw new OptimisticConcurrencyException(exception.Message, exception);
                }

                throw;
            }

            eventDataModels = eventDataModels
                .Zip(
                    ids,
                    (e, i) =>
                        {
                            e.GlobalSequenceNumber = i;
                            return e;
                        })
                .ToList();

            return eventDataModels;
        }

        public async Task<IReadOnlyCollection<ICommittedDomainEvent>> LoadCommittedEventsAsync(
            IIdentity id,
            int fromEventSequenceNumber,
            CancellationToken cancellationToken)
        {
            const string sql = @"
                SELECT
                    GlobalSequenceNumber, BatchId, AggregateId, AggregateName, Data, Metadata, AggregateSequenceNumber
                FROM EventFlow
                WHERE
                    AggregateId = @AggregateId AND
                    AggregateSequenceNumber >= @FromEventSequenceNumber
                ORDER BY
                    AggregateSequenceNumber ASC";
            var eventDataModels = await _connection.QueryAsync<EventDataModel>(
                Label.Named("mssql-fetch-events"), 
                cancellationToken,
                sql,
                new
                    {
                        AggregateId = id.Value,
                        FromEventSequenceNumber = fromEventSequenceNumber,
                    })
                .ConfigureAwait(false);
            return eventDataModels;
        }

        public Task<IAsyncEnumerable<IReadOnlyCollection<ICommittedDomainEvent>>> OpenStreamAsync(
            IIdentity id,
            int fromEventSequenceNumber,
            CancellationToken cancellationToken)
        {
            var asyncEnumerable = AsyncEnumerable.CreateEnumerable(() => new MsSqlEventEnumerator(
                _log,
                _connection,
                id,
                fromEventSequenceNumber,
                _eventFlowConfiguration.StreamingBatchSize));

            return Task.FromResult(asyncEnumerable);
        }

        public async Task DeleteEventsAsync(IIdentity id, CancellationToken cancellationToken)
        {
            const string sql = @"DELETE FROM EventFlow WHERE AggregateId = @AggregateId";
            var affectedRows = await _connection.ExecuteAsync(
                Label.Named("mssql-delete-aggregate"),
                cancellationToken,
                sql,
                new {AggregateId = id.Value})
                .ConfigureAwait(false);

            _log.Verbose(
                "Deleted entity with ID '{0}' by deleting all of its {1} events",
                id,
                affectedRows);
        }

        public async Task ImportEventsAsync(
            IAsyncEnumerable<IReadOnlyCollection<SerializedEvent>> serializedEventStream,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var numberOfEvents = await _connection.WithConnectionAsync(
                Label.Named("csdcdscs"),
                async (c, ct) =>
                {
                    if (!(c is SqlConnection sqlConnection)) throw new InvalidOperationException();

                    var i = 0;
                    await serializedEventStream.ForEachAwaitAsync(
                        async (events, _) =>
                        {
                            using (var sqlBulkCopy = new SqlBulkCopy(sqlConnection))
                            {
                                sqlBulkCopy.BatchSize = _eventFlowConfiguration.StreamingBatchSize;
                                sqlBulkCopy.DestinationTableName = "EventFlow";
                                var dataTable = new DataTable("EventFlow");

                                dataTable.Columns.Add(nameof(EventDataModel.GlobalSequenceNumber), typeof(long));
                                dataTable.Columns.Add(nameof(EventDataModel.BatchId), typeof(Guid));
                                dataTable.Columns.Add(nameof(EventDataModel.AggregateId), typeof(string));
                                dataTable.Columns.Add(nameof(EventDataModel.AggregateName), typeof(string));
                                dataTable.Columns.Add(nameof(EventDataModel.Data), typeof(string));
                                dataTable.Columns.Add(nameof(EventDataModel.Metadata), typeof(string));
                                dataTable.Columns.Add(nameof(EventDataModel.AggregateSequenceNumber), typeof(int));

                                foreach (var serializedEvent in events)
                                {
                                    dataTable.Rows.Add(
                                        DBNull.Value,
                                        Guid.Parse(serializedEvent.Metadata[MetadataKeys.BatchId]),
                                        serializedEvent.Metadata.AggregateId,
                                        serializedEvent.Metadata[MetadataKeys.AggregateName],
                                        serializedEvent.SerializedData,
                                        serializedEvent.SerializedMetadata,
                                        serializedEvent.AggregateSequenceNumber);
                                    i++;
                                }

                                await sqlBulkCopy.WriteToServerAsync(dataTable, cancellationToken).ConfigureAwait(false);
                            }
                        },
                        cancellationToken)
                        .ConfigureAwait(false);

                    return i;
                },
                cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();
            _log.Verbose(() => $"It took {stopwatch.Elapsed.TotalSeconds:0.##} seconds to import {numberOfEvents} events");
        }

        private class MsSqlEventEnumerator : IAsyncEnumerator<IReadOnlyCollection<ICommittedDomainEvent>>
        {
            private readonly ILog _log;
            private readonly IMsSqlConnection _connection;
            private readonly IIdentity _id;
            private readonly int _fromEventSequenceNumber;
            private readonly int _batchSize;
            private int _currentStartRowIndex;
            private bool _getNextBatch;

            private const string Sql = @"
                SELECT
                    GlobalSequenceNumber,
                    BatchId,
                    AggregateId,
                    AggregateName,
                    Data,
                    Metadata,
                    AggregateSequenceNumber,
                    EventRank
                FROM
                    (
                        SELECT
                            GlobalSequenceNumber,
                            BatchId,
                            AggregateId,
                            AggregateName,
                            Data,
                            Metadata,
                            AggregateSequenceNumber,
                            ROW_NUMBER() OVER(ORDER BY AggregateSequenceNumber ASC) AS EventRank
                        FROM
                            EventFlow
                        WHERE
                            AggregateId = @AggregateId AND
                            AggregateSequenceNumber >= @FromEventSequenceNumber
                    ) AS EventsWithRowNumber
                WHERE
                    EventRank >  @StartRowIndex AND
                    EventRank <= (@StartRowIndex + @MaximumRows)";

            public IReadOnlyCollection<ICommittedDomainEvent> Current { get; private set; }

            public MsSqlEventEnumerator(
                ILog log,
                IMsSqlConnection connection,
                IIdentity id,
                int fromEventSequenceNumber,
                int batchSize)
            {
                _log = log;
                _connection = connection;
                _id = id;
                _fromEventSequenceNumber = fromEventSequenceNumber;
                _batchSize = batchSize;
                _getNextBatch = true;
            }

            public async Task<bool> MoveNext(CancellationToken cancellationToken)
            {
                if (!_getNextBatch) return false;

                Current = await _connection.QueryAsync<EventDataModel>(
                        Label.Named("mssql-fetch-event-stream-batch"),
                        cancellationToken,
                        Sql,
                        new
                        {
                            AggregateId = _id.Value,
                            FromEventSequenceNumber = _fromEventSequenceNumber,
                            StartRowIndex = _currentStartRowIndex,
                            MaximumRows = _batchSize
                        })
                    .ConfigureAwait(false);

                _currentStartRowIndex += _batchSize;
                _getNextBatch = Current.Count >= _batchSize;

                _log.Verbose(() => $"Loaded {Current.Count} events in batch for aggregate '{_id.Value}'. Continue? {_getNextBatch}");

                return Current.Any();
            }

            public void Dispose()
            {
                // Nothing to do here
            }
        }
    }
}