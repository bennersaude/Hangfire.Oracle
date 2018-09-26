﻿using System;
using System.Linq;
using System.Threading;
using Hangfire.FluentNHibernateStorage.Entities;
using Hangfire.FluentNHibernateStorage.JobQueue;
using Xunit;

namespace Hangfire.FluentNHibernateStorage.Tests.JobQueue
{
    public class FluentNHibernateJobQueueTests : IClassFixture<TestDatabaseFixture>, IDisposable
    {
        public FluentNHibernateJobQueueTests()
        {
            _storage = ConnectionUtils.GetStorage();
        }

        public void Dispose()
        {
            _storage.Dispose();
        }

        private static readonly string[] DefaultQueues = {"default"};
        private readonly FluentNHibernateJobStorage _storage;

        private static CancellationToken CreateTimingOutCancellationToken()
        {
            var source = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return source.Token;
        }

        private static FluentNHibernateJobQueue CreateJobQueue(FluentNHibernateJobStorage storage)
        {
            return new FluentNHibernateJobQueue(storage, new FluentNHibernateStorageOptions());
        }

        [Fact]
        [CleanDatabase]
        public void Ctor_ThrowsAnException_WhenOptionsValueIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new FluentNHibernateJobQueue(_storage, null));

            Assert.Equal("options", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new FluentNHibernateJobQueue(null, new FluentNHibernateStorageOptions()));

            Assert.Equal("storage", exception.ParamName);
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldDeleteAJob()
        {
            // Arrange
            _storage.UseSession(session =>
            {
                session.DeleteAll<_JobQueue>();
                session.DeleteAll<_Job>();
                var newjob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                session.Insert(new _JobQueue {Job = newjob, Queue = "default"});
                session.Flush();
                var queue = CreateJobQueue(_storage);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                payload.RemoveFromQueue();

                // Assert
                Assert.NotNull(payload);

                var jobInQueue = session.Query<_JobQueue>().SingleOrDefault();
                Assert.Null(jobInQueue);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldFetchAJob_FromTheSpecifiedQueue()
        {
            // Arrange
            _storage.UseSession(session =>
            {
                var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                var newJobQueue = new _JobQueue {Job = newJob, Queue = "default"};
                session.Insert(newJobQueue);
                session.Flush();
                session.Clear();
 
                var queue = CreateJobQueue(_storage);

                // Act
                var payload = (FluentNHibernateFetchedJob) queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.Equal(newJob.Id.ToString(), payload.JobId);
                Assert.Equal("default", payload.Queue);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldFetchATimedOutJobs_FromTheSpecifiedQueue()
        {
            // Arrange
            _storage.UseSession(session =>
            {
                var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                session.Insert(new _JobQueue
                {
                    Job = newJob,
                    FetchedAt = _storage.UtcNow.AddDays(-1),
                    Queue = "default"
                });
                session.Flush();
                var queue = CreateJobQueue(_storage);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                Assert.NotEmpty(payload.JobId);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldFetchJobs_FromMultipleQueues()
        {
            _storage.UseSession(session =>
            {
                var queueNames = new[] { "critical", "default" };
                foreach (var queueName in queueNames)
                {
                    var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                    session.Insert(new _JobQueue
                    {
                        Job = newJob,
                        Queue = queueName
                    });
                }
                session.Flush();


                var queue = CreateJobQueue(_storage);

                
                var critical = (FluentNHibernateFetchedJob) queue.Dequeue(
                    queueNames,
                    CreateTimingOutCancellationToken());

                Assert.NotNull(critical.JobId);
                Assert.Equal("critical", critical.Queue);

                var @default = (FluentNHibernateFetchedJob) queue.Dequeue(
                    queueNames,
                    CreateTimingOutCancellationToken());

                Assert.NotNull(@default.JobId);
                Assert.Equal("default", @default.Queue);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldFetchJobs_OnlyFromSpecifiedQueues()
        {
            _storage.UseSession(session =>
            {
                session.DeleteAll<_JobQueue>();
                session.DeleteAll<_Job>();
                var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                session.Insert(new _JobQueue
                {
                    Job = newJob,
                    Queue = "critical"
                });
                session.Flush();

                var queue = CreateJobQueue(_storage);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(
                        DefaultQueues,
                        CreateTimingOutCancellationToken()));
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldSetFetchedAt_OnlyForTheFetchedJob()
        {
            _storage.UseSession(session =>
            {
                // Arrange
                session.DeleteAll<_JobQueue>();
                session.DeleteAll<_Job>();
                for (var i = 0; i < 2; i++)
                {
                    var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);
                    session.Insert(new _JobQueue
                    {
                        Job = newJob,
                        Queue = "default"
                    });
                }
                session.Flush();

                var queue = CreateJobQueue(_storage);

                // Act
                var payload = queue.Dequeue(
                    DefaultQueues,
                    CreateTimingOutCancellationToken());

                // Assert
                var otherJobFetchedAt = session.Query<_JobQueue>()
                    .Where(i => i.Job.Id != long.Parse(payload.JobId))
                    .Select(i => i.FetchedAt)
                    .Single();

                Assert.Null(otherJobFetchedAt);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsEmpty()
        {
            _storage.UseSession(session =>
            {
                var queue = CreateJobQueue(_storage);

                var exception = Assert.Throws<ArgumentException>(
                    () => queue.Dequeue(new string[0], CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldThrowAnException_WhenQueuesCollectionIsNull()
        {
            _storage.UseSession(session =>
            {
                var queue = CreateJobQueue(_storage);

                var exception = Assert.Throws<ArgumentNullException>(
                    () => queue.Dequeue(null, CreateTimingOutCancellationToken()));

                Assert.Equal("queues", exception.ParamName);
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ShouldWaitIndefinitely_WhenThereAreNoJobs()
        {
            _storage.UseSession(session =>
            {
                var cts = new CancellationTokenSource(200);
                var queue = CreateJobQueue(_storage);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            });
        }

        [Fact]
        [CleanDatabase]
        public void Dequeue_ThrowsOperationCanceled_WhenCancellationTokenIsSetAtTheBeginning()
        {
            _storage.UseSession(session =>
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                var queue = CreateJobQueue(_storage);

                Assert.Throws<OperationCanceledException>(
                    () => queue.Dequeue(DefaultQueues, cts.Token));
            });
        }

        [Fact]
        [CleanDatabase]
        public void Enqueue_AddsAJobToTheQueue()
        {
            _storage.UseSession(session =>
            {
                session.DeleteAll<_JobQueue>();
                session.Flush();
                session.Clear();
                var newJob = FluentNHibernateWriteOnlyTransactionTests.InsertNewJob(session);

                var queue = CreateJobQueue(_storage);

                queue.Enqueue(session, "default",newJob.Id.ToString());

                var record = session.Query<_JobQueue>().Single();
                Assert.Equal(newJob.Id, record.Job.Id);
                Assert.Equal("default", record.Queue);
                Assert.Null(record.FetchedAt);
            });
        }
    }
}