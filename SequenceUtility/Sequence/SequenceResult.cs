namespace Sequence
{
    public class SequenceResult
    {
        public string Name { get; set; }
        public SequenceCore Core { get; set; }
        public bool Success { get; set; }
        public string[] Messages { get; set; }
    }
}
