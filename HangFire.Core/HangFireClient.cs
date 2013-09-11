﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace HangFire
{
    /// <summary>
    /// Represents a top-level class for enqueuing jobs.
    /// </summary>
    public class HangFireClient : IDisposable
    {
        private static readonly HangFireClient Instance = new HangFireClient(
            HangFireConfiguration.Current.ClientFilters);

        static HangFireClient()
        {
        }

        public static string PerformAsync<TJob>()
        {
            return PerformAsync<TJob>(null);
        }

        public static string PerformAsync<TJob>(object args)
        {
            return PerformAsync(typeof(TJob), args);
        }

        public static string PerformAsync(Type jobType, object args = null)
        {
            return Instance.Async(jobType, args);
        }

        public static string PerformIn<TJob>(TimeSpan interval)
        {
            return PerformIn<TJob>(interval, null);
        }

        public static string PerformIn<TJob>(TimeSpan interval, object args)
        {
            return PerformIn(interval, typeof(TJob), args);
        }

        public static string PerformIn(TimeSpan interval, Type jobType, object args = null)
        {
            return Instance.In(interval, jobType, args);
        }

        private readonly RedisClient _client = new RedisClient();
        private readonly IEnumerable<IClientFilter> _filters;

        internal HangFireClient(IEnumerable<IClientFilter> filters)
        {
            _filters = filters;
        }

        public string Async(Type jobType, object args = null)
        {
            if (jobType == null)
            {
                throw new ArgumentNullException("jobType");
            }

            var jobId = GenerateId();
            var job = InitializeJob(jobType, args);

            Action enqueueAction = () =>
            {
                var queueName = JobHelper.GetQueueName(jobType);

                lock (_client)
                {
                    _client.TryToDo(storage => storage.EnqueueJob(queueName, jobId, job), throwOnError: true);
                }
            };

            InvokeFilters(jobId, job, enqueueAction);

            return jobId;
        }

        public string In(TimeSpan interval, Type jobType, object args = null)
        {
            if (jobType == null)
            {
                throw new ArgumentNullException("jobType");
            }

            if (interval != interval.Duration())
            {
                throw new ArgumentOutOfRangeException("interval", "Interval value can not be negative.");
            }

            if (interval.Equals(TimeSpan.Zero))
            {
                return Async(jobType, args);
            }

            var at = DateTime.UtcNow.Add(interval).ToTimestamp();

            var jobId = GenerateId();
            var job = InitializeJob(jobType, args);

            Action enqueueAction = () =>
            {
                lock (_client)
                {
                    _client.TryToDo(
                        storage => storage.ScheduleJob(jobId, job, at),
                        throwOnError: true);
                }
            };

            InvokeFilters(jobId, job, enqueueAction);

            return jobId;
        }

        public void Dispose()
        {
            _client.Dispose();
        }

        private Dictionary<string, string> InitializeJob(Type jobType, object args)
        {
            var job = new Dictionary<string, string>();
            job["Type"] = jobType.AssemblyQualifiedName;
            job["Args"] = SerializeArgs(args);

            return job;
        }

        private string GenerateId()
        {
            return Guid.NewGuid().ToString();
        }

        private string SerializeArgs(object args)
        {
            if (args == null) return null;

            var dictionary = new Dictionary<string, string>();

            foreach (PropertyDescriptor descriptor in TypeDescriptor.GetProperties(args))
            {
                var obj2 = descriptor.GetValue(args);
                string value = null;

                if (obj2 != null)
                {
                    // TODO: handle conversion exception and display it in a friendly way.
                    var converter = TypeDescriptor.GetConverter(obj2.GetType());
                    value = converter.ConvertToInvariantString(obj2);
                }

                dictionary.Add(descriptor.Name.ToLowerInvariant(), value);
            }

            return JsonHelper.Serialize(dictionary);
        }

        private void InvokeFilters(
            string jobId,
            Dictionary<string, string> job,
            Action enqueueAction)
        {
            var commandAction = enqueueAction;

            var entries = _filters.ToList();
            entries.Reverse();

            foreach (var entry in entries)
            {
                var currentEntry = entry;

                var filterContext = new ClientFilterContext(jobId, job, commandAction);
                commandAction = () => currentEntry.ClientFilter(filterContext);
            }

            commandAction();
        }
    }
}