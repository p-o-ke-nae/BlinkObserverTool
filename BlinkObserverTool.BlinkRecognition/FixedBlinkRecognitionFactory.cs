using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

public sealed class FixedBlinkRecognitionFactory : IRecognitionMethodFactory
{
    public const string ComponentId = "builtin.recognition.blink-fixed-template";

    public ComponentDescriptor Descriptor { get; } = new(
        ComponentId,
        "瞬き固定テンプレート認識",
        "セットアップで採取した開眼・閉眼テンプレートを固定し、正規化相関で判定します。",
        [
            new ParameterDefinition("EyeX", "目 ROI X", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeY", "目 ROI Y", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("EyeWidth", "目 ROI 幅", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("EyeHeight", "目 ROI 高さ", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("SearchX", "追従範囲 X", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("SearchY", "追従範囲 Y", ParameterValueKind.Integer, "0", Step: 1),
            new ParameterDefinition("SearchWidth", "追従範囲 幅", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("SearchHeight", "追従範囲 高さ", ParameterValueKind.Integer, "0", Minimum: 1, Step: 1),
            new ParameterDefinition("SmoothingFrames", "平滑化フレーム数", ParameterValueKind.Integer, "2", Minimum: 1, Maximum: 8, Step: 1),
            new ParameterDefinition("MinClosedFrames", "閉眼最小フレーム数", ParameterValueKind.Integer, "1", Minimum: 1, Maximum: 8, Step: 1),
            new ParameterDefinition("MinimumCorrelation", "最低一致度", ParameterValueKind.Decimal, "0.45", Minimum: -1, Maximum: 1, Step: 0.01),
            new ParameterDefinition("MinimumCorrelationMargin", "相関差", ParameterValueKind.Decimal, "0.05", Minimum: 0, Maximum: 2, Step: 0.01),
            new ParameterDefinition("RearmOpenFrames", "復帰後の連続開眼数", ParameterValueKind.Integer, "3", Minimum: 1, Maximum: 30, Step: 1),
            new ParameterDefinition("ModelSchemaVersion", "モデル スキーマ版", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelInputWidth", "モデル入力幅", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelInputHeight", "モデル入力高さ", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelEyeX", "モデル ROI X", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelEyeY", "モデル ROI Y", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelEyeWidth", "モデル ROI 幅", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelEyeHeight", "モデル ROI 高さ", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelTemplateWidth", "テンプレート幅", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelTemplateHeight", "テンプレート高さ", ParameterValueKind.Integer, ""),
            new ParameterDefinition("ModelPreprocessing", "前処理条件", ParameterValueKind.Text, ""),
            new ParameterDefinition("OpenTemplateBase64", "開眼テンプレート (Base64)", ParameterValueKind.Text, ""),
            new ParameterDefinition("ClosedTemplateBase64", "閉眼テンプレート (Base64)", ParameterValueKind.Text, ""),
            new ParameterDefinition("ModelHash", "モデル SHA-256", ParameterValueKind.Text, "")
        ]);

    public IRecognitionMethod Create(IReadOnlyDictionary<string, string> parameters)
    {
        FixedBlinkModel? model = null;
        FixedBlinkModelException? modelError = null;
        try
        {
            model = FixedBlinkModel.Load(parameters);
        }
        catch (FixedBlinkModelException exception)
        {
            modelError = exception;
        }

        return new FixedBlinkRecognizer(
            parameters.GetRoi("Eye"),
            parameters.GetRoi("Search"),
            parameters.GetInt32("SmoothingFrames", 2),
            parameters.GetInt32("MinClosedFrames", 1),
            model,
            modelError);
    }

    public static string GetModelStatus(IReadOnlyDictionary<string, string> parameters)
    {
        try
        {
            var model = FixedBlinkModel.Load(parameters);
            var configured = parameters.GetRoi("Eye");
            return configured == model.EyeRegion
                ? $"学習済み (schema {model.SchemaVersion}, {model.TemplateWidth}x{model.TemplateHeight})"
                : "エラー: ROI が学習時から変更されています。再セットアップしてください。";
        }
        catch (FixedBlinkModelException exception)
        {
            return $"セットアップが必要: {exception.Message}";
        }
    }
}
