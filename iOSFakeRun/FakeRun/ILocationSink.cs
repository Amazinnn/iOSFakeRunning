namespace iOSFakeRun.FakeRun;

/// <summary>
/// Where simulated positions are delivered. Kept separate from the device connection so the run
/// engine can be driven by a test double with no iPhone attached.
/// </summary>
internal interface ILocationSink
{
    bool Set(double latitude, double longitude);

    /// <summary>
    /// Why the last Set or Reset failed, phrased for the user, or empty when nothing has failed
    /// yet. The run loop folds this into its failure message so the advice matches the stage the
    /// write actually died at.
    /// </summary>
    string LastFailure { get; }
}
