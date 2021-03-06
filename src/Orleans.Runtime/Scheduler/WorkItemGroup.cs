using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Internal;

namespace Orleans.Runtime.Scheduler
{
    [DebuggerDisplay("WorkItemGroup Name={Name} State={state}")]
    internal class WorkItemGroup : IWorkItem
    {
        private enum WorkGroupStatus
        {
            Waiting = 0,
            Runnable = 1,
            Running = 2
        }

        private readonly ILogger log;
        private readonly OrleansTaskScheduler masterScheduler;
        private WorkGroupStatus state;
        private readonly Object lockable;
        private readonly Queue<Task> workItems;

        private long totalItemsEnQueued;    // equals total items queued, + 1
        private long totalItemsProcessed;
        private TimeSpan totalQueuingDelay;

        private Task currentTask;
        private DateTime currentTaskStarted;
        private long shutdownSinceTimestamp;
        private long lastShutdownWarningTimestamp;

        private readonly QueueTrackingStatistic queueTracking;
        private readonly long quantumExpirations;
        private readonly int workItemGroupStatisticsNumber;
        private readonly CancellationToken cancellationToken;
        private readonly SchedulerStatisticsGroup schedulerStatistics;

        internal ActivationTaskScheduler TaskScheduler { get; private set; }

        public DateTime TimeQueued { get; set; }

        public TimeSpan TimeSinceQueued
        {
            get { return Utils.Since(TimeQueued); }
        }

        public ISchedulingContext SchedulingContext { get; set; }

        public bool IsSystemPriority
        {
            get { return SchedulingUtils.IsSystemPriorityContext(SchedulingContext); }
        }

        internal bool IsSystemGroup
        {
            get { return SchedulingUtils.IsSystemContext(SchedulingContext); }
        }

        public string Name { get { return SchedulingContext == null ? "unknown" : SchedulingContext.Name; } }

        internal int ExternalWorkItemCount
        {
            get { lock (lockable) { return WorkItemCount; } }
        }

        private Task CurrentTask
        {
            get => currentTask;
            set
            {
                currentTask = value;
                currentTaskStarted = DateTime.UtcNow;
            }
        }

        private int WorkItemCount
        {
            get { return workItems.Count; }
        }

        internal float AverageQueueLength
        {
            get
            {
                return 0;
            }
        }

        internal float NumEnqueuedRequests
        {
            get
            {
                return 0;
            }
        }

        internal float ArrivalRate
        {
            get
            {
                return 0;
            }
        }

        private bool HasWork => this.WorkItemCount != 0;

        private bool IsShutdown => this.shutdownSinceTimestamp > 0;

        // This is the maximum number of work items to be processed in an activation turn. 
        // If this is set to zero or a negative number, then the full work queue is drained (MaxTimePerTurn allowing).
        private const int MaxWorkItemsPerTurn = 0; // Unlimited

        // This is the maximum number of waiting threads (blocked in WaitForResponse) allowed
        // per ActivationWorker. An attempt to wait when there are already too many threads waiting
        // will result in a TooManyWaitersException being thrown.
        //private static readonly int MaxWaitingThreads = 500;
        
        internal WorkItemGroup(
            OrleansTaskScheduler sched,
            ISchedulingContext schedulingContext,
            ILoggerFactory loggerFactory,
            CancellationToken ct,
            SchedulerStatisticsGroup schedulerStatistics,
            IOptions<StatisticsOptions> statisticsOptions)
        {
            masterScheduler = sched;
            SchedulingContext = schedulingContext;
            cancellationToken = ct;
            this.schedulerStatistics = schedulerStatistics;
            state = WorkGroupStatus.Waiting;
            workItems = new Queue<Task>();
            lockable = new Object();
            totalItemsEnQueued = 0;
            totalItemsProcessed = 0;
            totalQueuingDelay = TimeSpan.Zero;
            quantumExpirations = 0;
            TaskScheduler = new ActivationTaskScheduler(this, loggerFactory);
            log = IsSystemPriority ? loggerFactory.CreateLogger($"{this.GetType().Namespace} {Name}.{this.GetType().Name}") : loggerFactory.CreateLogger<WorkItemGroup>();

            if (schedulerStatistics.CollectShedulerQueuesStats)
            {
                queueTracking = new QueueTrackingStatistic("Scheduler." + SchedulingContext.Name, statisticsOptions);
                queueTracking.OnStartExecution();
            }

            if (schedulerStatistics.CollectPerWorkItemStats)
            {
                workItemGroupStatisticsNumber = schedulerStatistics.RegisterWorkItemGroup(SchedulingContext.Name, SchedulingContext,
                    () =>
                    {
                        var sb = new StringBuilder();
                        lock (lockable)
                        {

                            sb.Append("QueueLength = " + WorkItemCount);
                            sb.Append(String.Format(", State = {0}", state));
                            if (state == WorkGroupStatus.Runnable)
                                sb.Append(String.Format("; oldest item is {0} old", workItems.Count >= 0 ? workItems.Peek().ToString() : "null"));
                        }
                        return sb.ToString();
                    });
            }
        }

        /// <summary>
        /// Adds a task to this activation.
        /// If we're adding it to the run list and we used to be waiting, now we're runnable.
        /// </summary>
        /// <param name="task">The work item to add.</param>
        public void EnqueueTask(Task task)
        {
#if DEBUG
            if (log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace(
                    "EnqueueWorkItem {Task} into {SchedulingContext} when TaskScheduler.Current={TaskScheduler}",
                    task,
                    this.SchedulingContext,
                    System.Threading.Tasks.TaskScheduler.Current);
            }
#endif

            if (this.IsShutdown)
            {
                if (this.cancellationToken.IsCancellationRequested)
                {
                    // If the system is shutdown, do not schedule the task.
                    return;
                }

                // Log diagnostics and continue to schedule the task.
                LogEnqueueOnStoppedScheduler(task);
            }

            lock (lockable)
            {
                long thisSequenceNumber = totalItemsEnQueued++;
                int count = WorkItemCount;

                workItems.Enqueue(task);
                int maxPendingItemsLimit = masterScheduler.MaxPendingItemsSoftLimit;
                if (maxPendingItemsLimit > 0 && count > maxPendingItemsLimit)
                {
                    log.LogWarning(
                        (int)ErrorCode.SchedulerTooManyPendingItems,
                        "{PendingWorkItemCount} pending work items for group {WorkGroupName}, exceeding the warning threshold of {WarningThreshold}",
                        count,
                        this.Name,
                        maxPendingItemsLimit);
                }
                if (state != WorkGroupStatus.Waiting) return;

                state = WorkGroupStatus.Runnable;
#if DEBUG
                if (log.IsEnabled(LogLevel.Trace))
                {
                    log.LogTrace(
                        "Add to RunQueue {Task}, #{SequenceNumber}, onto {SchedulingContext}",
                        task,
                        thisSequenceNumber,
                        SchedulingContext);
                }
#endif
                masterScheduler.ScheduleExecution(this);
            }
        }

        /// <summary>
        /// For debugger purposes only.
        /// </summary>
        internal IEnumerable<Task> GetScheduledTasks()
        {
            foreach (var task in this.workItems)
            {
                yield return task;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void LogEnqueueOnStoppedScheduler(Task task)
        {
            var now = ValueStopwatch.GetTimestamp();
            if (this.lastShutdownWarningTimestamp == 0)
            {
                this.log.LogDebug(
                    (int)ErrorCode.SchedulerEnqueueWorkWhenShutdown,
                     "Enqueuing task {Task} to a work item group which should have terminated. "
                    + "Likely reasons are that the task is not being 'awaited' properly or a TaskScheduler was captured and is being used to schedule tasks "
                    + "after a grain has been deactivated.\nWorkItemGroup: {Status}\nTask.AsyncState: {TaskState}\n{Stack}",
                    OrleansTaskExtentions.ToString(task),
                    this.DumpStatus(),
                    task.AsyncState,
                    Utils.GetStackTrace());

                this.lastShutdownWarningTimestamp = now;
            }
            else if (ValueStopwatch.FromTimestamp(this.lastShutdownWarningTimestamp, now).Elapsed > this.masterScheduler.StoppedWorkItemGroupWarningInterval)
            {
                // Upgrade the warning to an error after 1 minute, include a stack trace, and continue to log up to once per minute.
                this.log.LogError(
                    (int)ErrorCode.SchedulerEnqueueWorkWhenShutdown,
                    "Enqueuing task {Task} to a work item group which should have terminated. "
                    + "Likely reasons are that the task is not being 'awaited' properly or a TaskScheduler was captured and is being used to schedule tasks "
                    + "after a grain has been deactivated.\nWorkItemGroup: {Status}\nTask.AsyncState: {TaskState}\n{Stack}",
                    OrleansTaskExtentions.ToString(task),
                    this.DumpStatus(),
                    task.AsyncState,
                    Utils.GetStackTrace());

                this.lastShutdownWarningTimestamp = now;
            }
        }

        /// <summary>
        /// Shuts down this work item group so that it will not process any additional work items, even if they
        /// have already been queued.
        /// </summary>
        internal void Stop()
        {
            lock (lockable)
            {
                if (this.HasWork)
                {
                    ReportWorkGroupProblem(
                        String.Format("WorkItemGroup is being shutdown while still active. workItemCount = {0}."
                        + "The likely reason is that the task is not being 'awaited' properly.", WorkItemCount),
                        ErrorCode.SchedulerWorkGroupStopping);
                }

                if (this.IsShutdown)
                {
                    log.LogWarning(
                        (int)ErrorCode.SchedulerWorkGroupShuttingDown,
                        "WorkItemGroup is already shutting down {WorkItemGroup}",
                        this.ToString());
                    return;
                }

                this.shutdownSinceTimestamp = ValueStopwatch.GetTimestamp();

                if (this.schedulerStatistics.CollectPerWorkItemStats)
                    this.schedulerStatistics.UnRegisterWorkItemGroup(workItemGroupStatisticsNumber);

                if (this.schedulerStatistics.CollectShedulerQueuesStats)
                    queueTracking.OnStopExecution();
            }
        }

        public WorkItemType ItemType
        {
            get { return WorkItemType.WorkItemGroup; }
        }

        // Execute one or more turns for this activation. 
        // This method is always called in a single-threaded environment -- that is, no more than one
        // thread will be in this method at once -- but other asynch threads may still be queueing tasks, etc.
        public void Execute()
        {
            try
            {
                RuntimeContext.SetExecutionContext(this.SchedulingContext);

                // Process multiple items -- drain the applicationMessageQueue (up to max items) for this physical activation
                int count = 0;
                var stopwatch = ValueStopwatch.StartNew();
                do
                {
                    lock (lockable)
                    {
                        state = WorkGroupStatus.Running;

                        // Check the cancellation token (means that the silo is stopping)
                        if (cancellationToken.IsCancellationRequested)
                        {
                            this.log.LogWarning(
                                (int)ErrorCode.SchedulerSkipWorkCancelled,
                                "Thread {Thread} is exiting work loop due to cancellation token. WorkItemGroup: {WorkItemGroup}, Have {WorkItemCount} work items in the queue",
                                Thread.CurrentThread.ToString(),
                                this.ToString(),
                                this.WorkItemCount);

                            return;
                        }
                    }

                    // Get the first Work Item on the list
                    Task task;
                    lock (lockable)
                    {
                        if (workItems.Count > 0)
                            CurrentTask = task = workItems.Dequeue();
                        else // If the list is empty, then we're done
                            break;
                    }

#if DEBUG
                    if (log.IsEnabled(LogLevel.Trace))
                    {
                        log.LogTrace(
                        "About to execute task {Task} in SchedulingContext={SchedulingContext}",
                        OrleansTaskExtentions.ToString(task),
                        this.SchedulingContext);
                    }
#endif
                    var taskStart = stopwatch.Elapsed;

                    try
                    {
                        TaskScheduler.RunTask(task);
                    }
                    catch (Exception ex)
                    {
                        this.log.LogError(
                            (int)ErrorCode.SchedulerExceptionFromExecute,
                            ex,
                            "Worker thread caught an exception thrown from Execute by task {Task}. Exception: {Exception}",
                            OrleansTaskExtentions.ToString(task),
                            ex);
                        throw;
                    }
                    finally
                    {
                        totalItemsProcessed++;
                        var taskLength = stopwatch.Elapsed - taskStart;
                        if (taskLength > OrleansTaskScheduler.TurnWarningLengthThreshold)
                        {
                            this.schedulerStatistics.NumLongRunningTurns.Increment();
                            this.log.LogWarning(
                                (int)ErrorCode.SchedulerTurnTooLong3,
                                "Task {Task} in WorkGroup {SchedulingContext} took elapsed time {Duration} for execution, which is longer than {TurnWarningLengthThreshold}. Running on thread {Thread}",
                                OrleansTaskExtentions.ToString(task),
                                this.SchedulingContext.ToString(),
                                taskLength.ToString("g"),
                                OrleansTaskScheduler.TurnWarningLengthThreshold,
                                Thread.CurrentThread.ToString());
                        }

                        CurrentTask = null;
                    }
                    count++;
                }
                while (((MaxWorkItemsPerTurn <= 0) || (count <= MaxWorkItemsPerTurn)) &&
                    ((masterScheduler.SchedulingOptions.ActivationSchedulingQuantum <= TimeSpan.Zero) || (stopwatch.Elapsed < masterScheduler.SchedulingOptions.ActivationSchedulingQuantum)));
            }
            catch (Exception ex)
            {
                this.log.LogError(
                    (int)ErrorCode.Runtime_Error_100032,
                    ex,
                    "Worker thread {Thread} caught an exception thrown from IWorkItem.Execute: {Exception}",
                    Thread.CurrentThread,
                    ex);
            }
            finally
            {
                // Now we're not Running anymore. 
                // If we left work items on our run list, we're Runnable, and need to go back on the silo run queue; 
                // If our run list is empty, then we're waiting.
                lock (lockable)
                {
                    if (WorkItemCount > 0 && !this.IsShutdown)
                    {
                        state = WorkGroupStatus.Runnable;
                        masterScheduler.ScheduleExecution(this);
                    }
                    else
                    {
                        state = WorkGroupStatus.Waiting;
                    }
                }

                RuntimeContext.ResetExecutionContext();
            }
        }

        public override string ToString()
        {
            return String.Format("{0}WorkItemGroup:Name={1},WorkGroupStatus={2}",
                IsSystemGroup ? "System*" : "",
                Name,
                state);
        }

        public string DumpStatus()
        {
            lock (lockable)
            {
                var sb = new StringBuilder();
                sb.Append(this);
                sb.AppendFormat(". Currently QueuedWorkItems={0}; Total EnQueued={1}; Total processed={2}; Quantum expirations={3}; ",
                    WorkItemCount, totalItemsEnQueued, totalItemsProcessed, quantumExpirations);
                if (CurrentTask != null)
                {
                    sb.AppendFormat(" Executing Task Id={0} Status={1} for {2}.",
                        CurrentTask.Id, CurrentTask.Status, Utils.Since(currentTaskStarted));
                }

                if (AverageQueueLength > 0)
                {
                    sb.AppendFormat("average queue length at enqueue: {0}; ", AverageQueueLength);
                    if (!totalQueuingDelay.Equals(TimeSpan.Zero) && totalItemsProcessed > 0)
                    {
                        sb.AppendFormat("average queue delay: {0}ms; ", totalQueuingDelay.Divide(totalItemsProcessed).TotalMilliseconds);
                    }
                }

                sb.AppendFormat("TaskRunner={0}; ", TaskScheduler);
                if (SchedulingContext != null)
                {
                    sb.AppendFormat("Detailed SchedulingContext=<{0}>", SchedulingContext.DetailedStatus());
                }
                return sb.ToString();
            }
        }

        private void ReportWorkGroupProblem(string what, ErrorCode errorCode)
        {
            var msg = string.Format("{0} {1}", what, DumpStatus());
            log.Warn(errorCode, msg);
        }
    }
}