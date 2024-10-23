using SequenceUtility.Sequence.SequenceEventArgs;

namespace Sequence
{
    public class SequencePack : SequenceCore
    {
        private SequenceResult _stopResult = null;
        private readonly object _processSyncRoot = new();

        public override event EventHandler<SequenceCoreAsyncEventArgs> OnStartAsync;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStarted;
        public override event EventHandler<SequenceCoreProcEventArgs> OnFinished;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopping;
        public override event EventHandler<SequenceCoreProcEventArgs> OnStopped;
        public override event EventHandler<SequenceCoreErrorEventArgs> OnErrorOccurred;

        private SequenceCore[][] _sequences;
        public int CurrentUnitsIndex { get; private set; }

        public SequencePack(string name,
                            SequenceCore.SequenceCondition startCondition = null,
                            SequenceCore.SequenceCondition finishCondition = null,
                            SequenceCore.SequenceCondition stopProcess = null,
                            object packSyncRoot = null,
                            object payloadSyncRoot = null)
        {
            Name = name;
            StartCondition = startCondition;
            FinishCondition = finishCondition;
            StopProcess = stopProcess;
            PayloadSyncRoot = payloadSyncRoot ?? new();
            SyncRoot = packSyncRoot ?? new();

            _sequences = Array.Empty<SequenceCore[]>();

            if (StartCondition == null)
            {
                StartCondition = (sender, e) => new SequenceResult { Success = true };
            }

            if (FinishCondition == null)
            {
                FinishCondition = (sender, e) => new SequenceResult { Success = true };
            }
        }

        public void AddSequence(params SequenceCore[] sequenceUnits)
        {
            if (sequenceUnits != null)
            {
                AddSequences(sequenceUnits.Select(x => new SequenceCore[] { x }).ToArray());
            }
        }

        public void AddSequences(params SequenceCore[][] sequenceUnits)
        {
            foreach (var units in sequenceUnits)
            {
                _sequences = _sequences.Append(units).ToArray();
            }
        }

        public SequenceCore[][] GetSequences()
        {
            return _sequences.ToArray();
        }

        public void UpdateSequences(IEnumerable<SequenceCore[]> sequences)
        {
            _sequences = sequences.ToArray();
        }

        public override Task<SequenceResult> StartAsync(Dictionary<string, object> payload)
        {
            AsyncTask = Task.Run(() => Start(payload));
            OnStartAsync?.Invoke(this, new SequenceCoreAsyncEventArgs(AsyncTask));
            return AsyncTask;
        }

        public override SequenceResult Start(Dictionary<string, object> payload)
        {
            lock (SyncRoot)
            {
                try
                {
                    SequenceResult result = new()
                    {
                        Name = Name,
                        Core = this,
                        Success = false,
                        Messages = new string[0]
                    };

                    lock (_processSyncRoot)
                    {
                        Payload = payload;

                        if (State != SequenceState.Ready)
                        {
                            result = new SequenceResult
                            {
                                Name = Name,
                                Core = this,
                                Success = false,
                                Messages = new string[] { "No Ready state" }
                            };

                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }

                        // Verify Units
                        if (_sequences.Any(x => x == null || x.Length == 0))
                        {
                            result = new SequenceResult
                            {
                                Name = Name,
                                Core = this,
                                Success = false,
                                Messages = new string[] { "No sequence units" }
                            };

                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }

                        CurrentUnitsIndex = 0;

                        result = StartCondition(this, Payload);
                        result.Core = this;
                        result.Name = Name;

                        if (StopSignal > 0)
                        {
                            State = SequenceState.Stopped;

                            if (result.Messages != null)
                                result.Messages = result.Messages.Concat(
                                    _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                            else
                                result.Messages = _stopResult?.Messages?.ToArray();

                            result.Success = _stopResult?.Success ?? false;

                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                        else if (!result.Success)
                        {
                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                        else
                        {
                            State = SequenceState.Running;
                        }
                    }

                    OnStarted?.Invoke(this, new SequenceCoreProcEventArgs(
                        Name,
                        new SequenceResult
                        {
                            Name = Name,
                            Success = true
                        }
                    ));

                    List<List<Task<SequenceResult>>> taskLists = new();

                    while (CurrentUnitsIndex < _sequences.Length)
                    {

                        lock (_processSyncRoot)
                        {
                            if (StopSignal > 0)
                            {
                                State = SequenceState.Stopped;

                                if (result.Messages != null)
                                    result.Messages = result.Messages.Concat(
                                        _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                                else
                                    result.Messages = _stopResult?.Messages?.ToArray();

                                result.Success = _stopResult?.Success ?? false;

                                OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                                OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                                return result;
                            }
                        }

                        foreach (var seq in _sequences[CurrentUnitsIndex])
                        {
                            uint asyncIndex = seq.AsyncLevel + (uint)CurrentUnitsIndex;

                            while (taskLists.Count < _sequences.Length)
                            {
                                taskLists.Add(new());
                            }

                            if (asyncIndex >= taskLists.Count)
                                asyncIndex = (uint)(taskLists.Count - 1);

                            taskLists[(int)asyncIndex].Add(seq.StartAsync(payload));
                        }

                        var unitResults = Task.WhenAll(taskLists[CurrentUnitsIndex].ToArray()).Result;
                        var fails = unitResults.Where(x => x?.Success != true);

                        if (fails.Any())
                        {
                            SequenceResult failResult = new()
                            {
                                Name = Name,
                                Core = this,
                                Success = false,
                                Messages = fails.SelectMany(x => x.Messages ?? Array.Empty<string>()).ToArray()
                            };

                            State = SequenceState.Stopped;
                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, failResult));
                            OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, failResult));
                            return failResult;
                        }
                        else
                        {
                            result = new()
                            {
                                Name = Name,
                                Core = this,
                                Success = true,
                                Messages = unitResults.SelectMany(x => x.Messages ?? new string[0]).ToArray()
                            };
                        }

                        CurrentUnitsIndex++;
                    }

                    lock (_processSyncRoot)
                    {
                        if (StopSignal > 0)
                        {
                            State = SequenceState.Stopped;

                            if (result.Messages != null)
                                result.Messages = result.Messages.Concat(
                                    _stopResult?.Messages?.ToArray() ?? new string[0]).ToArray();
                            else
                                result.Messages = _stopResult?.Messages?.ToArray();

                            result.Success = _stopResult?.Success ?? false;

                            OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                            return result;
                        }
                    }

                    if (!result.Success)
                    {
                        State = SequenceState.Stopped;
                        OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        return result;
                    }
                    else
                    {
                        State = SequenceState.Done;
                    }

                    result = FinishCondition(this, Payload);
                    result.Core = this;
                    result.Name = Name;

                    if (!result.Success)
                    {
                        State = SequenceState.Stopped;
                        OnStopped?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                        return result;
                    }
                    else
                    {
                        State = SequenceState.Done;
                    }

                    OnFinished?.Invoke(this, new SequenceCoreProcEventArgs(Name, result));
                    return result;
                }
                catch (Exception ex)
                {
                    OnErrorOccurred?.Invoke(this, new() { Error = ex });

                    return new()
                    {
                        Name = Name,
                        Core = this,
                        Success = false,
                        Messages = new string[] { ex.ToString() }
                    };
                }
            }
        }

        public override void Stop()
        {
            lock (_processSyncRoot)
            {
                if (State != SequenceState.Running)
                {
                    State = SequenceState.Stopped;
                    StopSignal = 1;
                    _stopResult = null;
                }
                else
                {
                    StopSignal = 1;
                    State = SequenceState.Stopping;
                    OnStopping?.Invoke(this, new SequenceCoreProcEventArgs(Name, null));
                    _stopResult = StopProcess?.Invoke(this, Payload);

                    for (int i = 0; i <= CurrentUnitsIndex; i++)
                    {
                        foreach (var unit in _sequences[i])
                        {
                            unit.Stop();
                        }
                    }
                }
            }
        }
    }
}
