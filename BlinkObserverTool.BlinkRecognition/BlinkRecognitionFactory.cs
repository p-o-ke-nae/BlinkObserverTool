using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

public sealed class BlinkRecognitionFactory : IRecognitionMethodFactory
{
    public const string ComponentId = "builtin.recognition.blink-feature";

    public ComponentDescriptor Descriptor { get; } = new(
        ComponentId,
        "瞬き特徴認識",
        "暗く小さい目領域を局所正規化し、時間方向の変化から瞬きを検出します。",
        [
            new ParameterDefinition("EyeX", "目 ROI X", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeY", "目 ROI Y", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeWidth", "目 ROI 幅", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("EyeHeight", "目 ROI 高さ", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("SearchX", "追従範囲 X", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("SearchY", "追従範囲 Y", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("SearchWidth", "追従範囲 幅", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("SearchHeight", "追従範囲 高さ", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition(
                "Sensitivity",
                "感度",
                ParameterValueKind.Choice,
                "Standard",
                Options:
                [
                    new ParameterOption("Low", "低"),
                    new ParameterOption("Standard", "標準"),
                    new ParameterOption("High", "高")
                ]),
            new ParameterDefinition("SmoothingFrames", "平滑化フレーム数", ParameterValueKind.Integer, "3", Minimum: 1, Maximum: 8, Step: 1),
            new ParameterDefinition("MinClosedFrames", "閉眼最小フレーム数", ParameterValueKind.Integer, "1", Minimum: 1, Maximum: 8, Step: 1),
            new ParameterDefinition("CalibrationFrames", "開眼学習フレーム数", ParameterValueKind.Integer, "12", Minimum: 6, Maximum: 120, Step: 1),
            new ParameterDefinition("DetectionThreshold", "検出閾値 (0=自動)", ParameterValueKind.Decimal, "0", Minimum: 0, Maximum: 1, Step: 0.001)
        ]);

    public IRecognitionMethod Create(IReadOnlyDictionary<string, string> parameters)
    {
        return new BlinkRecognizer(
            parameters.GetRoi("Eye"),
            parameters.GetRoi("Search"),
            parameters.GetString("Sensitivity", "Standard"),
            parameters.GetInt32("SmoothingFrames", 3),
            parameters.GetInt32("MinClosedFrames", 1),
            parameters.GetInt32("CalibrationFrames", 12),
            parameters.GetDouble("DetectionThreshold"));
    }
}
