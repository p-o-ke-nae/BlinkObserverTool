using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Recognition.Core;

namespace BlinkObserverTool.BlinkRecognition;

/// <summary>
/// Multi-exemplar blink model. Open exemplars cover the idle sway of the character and closed exemplars cover
/// several blink poses. Exemplars are stored as packed BGR24 pixels at native resolution.
/// </summary>
public sealed record MultiExemplarBlinkModel(
    int SchemaVersion,
    int InputWidth,
    int InputHeight,
    RoiArea EyeRegion,
    int SearchRadius,
    string Preprocessing,
    IReadOnlyList<byte[]> OpenExemplars,
    IReadOnlyList<byte[]> ClosedExemplars,
    MultiExemplarBlinkThresholds Calibrated)
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentPreprocessing = "bgr24-native-ccoeffnormed";
    public const int MinimumSearchRadius = 2;
    public const int MaximumSearchRadius = 8;
    public const int MaximumOpenExemplars = 24;
    public const int MaximumClosedExemplars = 12;
    public const int MaximumTemplateSide = 160;
    public const int MaximumModelJsonLength = 2_000_000;
    internal const double MinimumExemplarStdDev = 2d;

    public const string ModelJsonKey = "MultiModelJson";
    public const string ModelHashKey = "MultiModelHash";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public int TemplateWidth => EyeRegion.Width;

    public int TemplateHeight => EyeRegion.Height;

    public int TemplateByteLength => EyeRegion.Width * EyeRegion.Height * 3;

    public string ToJson()
    {
        var dto = new ModelDto
        {
            SchemaVersion = SchemaVersion,
            InputWidth = InputWidth,
            InputHeight = InputHeight,
            EyeX = EyeRegion.X,
            EyeY = EyeRegion.Y,
            EyeWidth = EyeRegion.Width,
            EyeHeight = EyeRegion.Height,
            SearchRadius = SearchRadius,
            Preprocessing = Preprocessing,
            OpenExemplars = OpenExemplars.Select(Convert.ToBase64String).ToList(),
            ClosedExemplars = ClosedExemplars.Select(Convert.ToBase64String).ToList(),
            ClosedMarginThreshold = Calibrated.ClosedMarginThreshold,
            OpenSimilarityThreshold = Calibrated.OpenSimilarityThreshold,
            ClosedMinimumCorrelation = Calibrated.ClosedMinimumCorrelation
        };
        return JsonSerializer.Serialize(dto, SerializerOptions);
    }

    public static string CalculateHash(string modelJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelJson)));

    /// <summary>Returns the parameters to store in the recognition profile (model + calibrated runtime thresholds).</summary>
    public Dictionary<string, string> ToParameters()
    {
        var json = ToJson();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EyeX"] = EyeRegion.X.ToString(CultureInfo.InvariantCulture),
            ["EyeY"] = EyeRegion.Y.ToString(CultureInfo.InvariantCulture),
            ["EyeWidth"] = EyeRegion.Width.ToString(CultureInfo.InvariantCulture),
            ["EyeHeight"] = EyeRegion.Height.ToString(CultureInfo.InvariantCulture),
            ["ClosedMarginThreshold"] = Calibrated.ClosedMarginThreshold.ToString("R", CultureInfo.InvariantCulture),
            ["OpenSimilarityThreshold"] = Calibrated.OpenSimilarityThreshold.ToString("R", CultureInfo.InvariantCulture),
            ["ClosedMinimumCorrelation"] = Calibrated.ClosedMinimumCorrelation.ToString("R", CultureInfo.InvariantCulture),
            [ModelJsonKey] = json,
            [ModelHashKey] = CalculateHash(json)
        };
    }

    public static MultiExemplarBlinkModel Load(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue(ModelJsonKey, out var json) || string.IsNullOrWhiteSpace(json))
        {
            throw new FixedBlinkModelException("blink-model-missing", "複数パターン学習モデルがありません。瞬き観測セットアップの「複数パターン」タブを実行してください。");
        }

        if (json.Length > MaximumModelJsonLength)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルが大きすぎます。");
        }

        if (!parameters.TryGetValue(ModelHashKey, out var hash) || string.IsNullOrWhiteSpace(hash)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(hash.Trim().ToUpperInvariant()),
                Encoding.ASCII.GetBytes(CalculateHash(json))))
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルのハッシュが一致しません。再セットアップしてください。");
        }

        ModelDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ModelDto>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルの JSON が破損しています。", exception);
        }

        if (dto is null)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルが空です。");
        }

        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            throw new FixedBlinkModelException(
                "blink-model-version-unsupported",
                $"複数パターン学習モデルの版 {dto.SchemaVersion} はサポートされていません。再セットアップしてください。");
        }

        MultiExemplarBlinkModel model;
        try
        {
            model = new MultiExemplarBlinkModel(
                dto.SchemaVersion,
                dto.InputWidth,
                dto.InputHeight,
                new RoiArea(dto.EyeX, dto.EyeY, dto.EyeWidth, dto.EyeHeight),
                dto.SearchRadius,
                dto.Preprocessing ?? string.Empty,
                (dto.OpenExemplars ?? []).Select(Convert.FromBase64String).ToList(),
                (dto.ClosedExemplars ?? []).Select(Convert.FromBase64String).ToList(),
                new MultiExemplarBlinkThresholds(dto.ClosedMarginThreshold, dto.OpenSimilarityThreshold, dto.ClosedMinimumCorrelation));
        }
        catch (FormatException exception)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルのテンプレートが破損しています。", exception);
        }

        model.Validate();
        return model;
    }

    public void Validate()
    {
        if (InputWidth <= 0 || InputHeight <= 0 || EyeRegion.IsEmpty
            || EyeRegion.X < 0 || EyeRegion.Y < 0
            || EyeRegion.X + EyeRegion.Width > InputWidth || EyeRegion.Y + EyeRegion.Height > InputHeight
            || EyeRegion.Width > MaximumTemplateSide || EyeRegion.Height > MaximumTemplateSide
            || SearchRadius is < MinimumSearchRadius or > MaximumSearchRadius
            || Preprocessing != CurrentPreprocessing
            || OpenExemplars.Count is < 1 or > MaximumOpenExemplars
            || ClosedExemplars.Count is < 1 or > MaximumClosedExemplars
            || OpenExemplars.Concat(ClosedExemplars).Any(exemplar => exemplar.Length != TemplateByteLength)
            || !Calibrated.IsValid)
        {
            throw new FixedBlinkModelException("blink-model-corrupt", "複数パターン学習モデルの内容が不正です。再セットアップしてください。");
        }
    }

    public static string GetModelStatus(IReadOnlyDictionary<string, string> parameters)
    {
        try
        {
            var model = Load(parameters);
            return parameters.GetRoi("Eye") == model.EyeRegion
                ? $"学習済み (開眼 {model.OpenExemplars.Count} / 閉眼 {model.ClosedExemplars.Count} パターン, 探索 ±{model.SearchRadius}px)"
                : "エラー: ROI が学習時から変更されています。再セットアップしてください。";
        }
        catch (FixedBlinkModelException exception)
        {
            return $"セットアップが必要: {exception.Message}";
        }
    }

    private sealed class ModelDto
    {
        public int SchemaVersion { get; set; }
        public int InputWidth { get; set; }
        public int InputHeight { get; set; }
        public int EyeX { get; set; }
        public int EyeY { get; set; }
        public int EyeWidth { get; set; }
        public int EyeHeight { get; set; }
        public int SearchRadius { get; set; }
        public string? Preprocessing { get; set; }
        public List<string>? OpenExemplars { get; set; }
        public List<string>? ClosedExemplars { get; set; }
        public double ClosedMarginThreshold { get; set; }
        public double OpenSimilarityThreshold { get; set; }
        public double ClosedMinimumCorrelation { get; set; }
    }
}

/// <summary>
/// Decision thresholds. A frame is closed when closedMax ≥ ClosedMinimumCorrelation,
/// closedMax − openMax &gt; ClosedMarginThreshold and openMax &lt; OpenSimilarityThreshold.
/// </summary>
public sealed record MultiExemplarBlinkThresholds(
    double ClosedMarginThreshold,
    double OpenSimilarityThreshold,
    double ClosedMinimumCorrelation)
{
    public bool IsValid =>
        double.IsFinite(ClosedMarginThreshold) && ClosedMarginThreshold is >= -2d and <= 2d
        && double.IsFinite(OpenSimilarityThreshold) && OpenSimilarityThreshold is >= -1d and <= 1d
        && double.IsFinite(ClosedMinimumCorrelation) && ClosedMinimumCorrelation is >= -1d and <= 1d;
}
