using GMandE7BUSBPoorSolderingInspectionDevice.Models.Measurements;

namespace GMandE7BUSBPoorSolderingInspectionDevice.Models.Inspection;

/// <summary>检测状态变更事件参数</summary>
public class InspectionStateChangedEventArgs : EventArgs
{
    public InspectionState OldState { get; }
    public InspectionState NewState { get; }

    public InspectionStateChangedEventArgs(InspectionState oldState, InspectionState newState)
    {
        OldState = oldState;
        NewState = newState;
    }
}

/// <summary>单项检测开始事件参数</summary>
public class StepStartedEventArgs : EventArgs
{
    public int StepIndex { get; }
    public TestPointConfig TestPoint { get; }

    public StepStartedEventArgs(int stepIndex, TestPointConfig testPoint)
    {
        StepIndex = stepIndex;
        TestPoint = testPoint;
    }
}

/// <summary>单项检测完成事件参数</summary>
public class StepCompletedEventArgs : EventArgs
{
    public int StepIndex { get; }
    public TestPointConfig TestPoint { get; }
    public MeasurementResult Measurement { get; }

    public StepCompletedEventArgs(int stepIndex, TestPointConfig testPoint, MeasurementResult measurement)
    {
        StepIndex = stepIndex;
        TestPoint = testPoint;
        Measurement = measurement;
    }
}

/// <summary>检测完成事件参数</summary>
public class InspectionCompletedEventArgs : EventArgs
{
    public InspectionResult Result { get; }

    public InspectionCompletedEventArgs(InspectionResult result)
    {
        Result = result;
    }
}
