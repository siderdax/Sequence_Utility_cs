using Sequence;

namespace SequenceUtility.Sequence.SequenceEventArgs
{
    public class SequenceCoreProcEventArgs
    {
        public string Name { get; init; }
        public SequenceResult Result { get; init; }

        public SequenceCoreProcEventArgs(string name, SequenceResult result)
        {
            Name = name;
            Result = result;
        }
    }
}
