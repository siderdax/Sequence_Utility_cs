using Sequence;

namespace SequenceUtility.Sequence.SequenceEventArgs
{
    public class SequenceCoreAsyncEventArgs : EventArgs
    {
        Task<SequenceResult> Task { get; init; }

        public SequenceCoreAsyncEventArgs(Task<SequenceResult> task)
        {
            Task = task;
        }
    }
}
