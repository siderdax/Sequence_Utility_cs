using SequenceUtility.Sequence.SequenceEventArgs;

namespace Sequence
{
    public abstract class SequenceCore
    {
        public string Name { get; init; }
        public Task<SequenceResult> AsyncTask { get; protected set; } = null;
        public uint AsyncLevel { get; set; } = 0;
        public object SyncRoot { get; init; } = new();
        public int StopSignal { get; protected set; }
        public object ReservedObject { get; set; }

        public enum SequenceState
        {
            Ready,
            Running,
            Done,
            Stopping,
            Stopped,
            Paused
        }
        public SequenceState State { get; protected set; } = SequenceState.Ready;
        public Dictionary<string, object> Payload { get; set; } = null;
        public object PayloadSyncRoot { get; set; }

        public abstract event EventHandler<SequenceCoreAsyncEventArgs> OnStartAsync;
        public abstract event EventHandler<SequenceCoreProcEventArgs> OnStarted;
        public abstract event EventHandler<SequenceCoreProcEventArgs> OnFinished;
        public abstract event EventHandler<SequenceCoreProcEventArgs> OnStopping;
        public abstract event EventHandler<SequenceCoreProcEventArgs> OnStopped;
        public abstract event EventHandler<SequenceCoreErrorEventArgs> OnErrorOccurred;

        protected Func<SequenceCore, Dictionary<string, object>, SequenceResult> StartCondition;
        protected Func<SequenceCore, Dictionary<string, object>, SequenceResult> FinishCondition;
        protected Func<SequenceCore, Dictionary<string, object>, SequenceResult> WorkProcess;
        protected Func<SequenceCore, Dictionary<string, object>, SequenceResult> StopProcess;

        public abstract SequenceResult Start(Dictionary<string, object> payload);
        public abstract Task<SequenceResult> StartAsync(Dictionary<string, object> payload);
        public abstract void Stop();
    }
}
