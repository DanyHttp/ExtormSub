namespace ExtormSub.Core.ASR;

/// <param name="Bytes">Exact file size.</param>
/// <param name="Sha256">Pinned from the Hugging Face LFS metadata; every download is verified against it.</param>
/// <param name="Guide">Who the model is for, shown under it in Settings.</param>
public sealed record WhisperModel(string Id, string DisplayName, long Bytes, bool EnglishOnly, string Notes, string? Sha256 = null, string Guide = "")
{
    public long ApproxBytes => Bytes;
    public string FileName => $"ggml-{Id}.bin";
    public Uri DownloadUri => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}");
    public string SizeText => ApproxBytes >= 1L << 30 ? $"{ApproxBytes / (double)(1L << 30):0.0} GB" : $"{ApproxBytes >> 20} MB";
}

public enum GpuVendor { None, Nvidia, Amd, Intel, Other }

public sealed record GpuInfo(string Name, GpuVendor Vendor, long MemoryBytes)
{
    /// <summary>Intel iGPUs are usually slower than the CPU for whisper; Arc cards are not.</summary>
    public bool WorthUsing => Vendor is GpuVendor.Nvidia or GpuVendor.Amd
        || (Vendor == GpuVendor.Intel && Name.Contains("Arc", StringComparison.OrdinalIgnoreCase));
}

public sealed record HardwareInfo(
    string CpuName,
    int PhysicalCores,
    int LogicalCores,
    long TotalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus,
    bool HasVulkan,
    bool HasCuda)
{
    public GpuInfo? BestGpu => Gpus.Where(g => g.WorthUsing).OrderByDescending(g => g.MemoryBytes).FirstOrDefault();
}

public static class ModelCatalog
{
    public static IReadOnlyList<WhisperModel> All { get; } =
    [
        new("tiny.en", "Tiny (English)", 77_704_715, true, "Fastest, lowest accuracy", "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
            "Only for very old or weak PCs. Keeps up on almost anything, but often mishears names, accents and fast speech."),
        new("base.en-q5_1", "Base (English, quantized)", 59_721_011, true, "Fast on any CPU", "4baf70dd0d7c4247ba2b81fafd9c01005ac77c2f9ef064e00dcf195d0e2fdd2f",
            "For laptops and older processors without a usable graphics card. Almost as accurate as Base at less than half the size."),
        new("base.en", "Base (English)", 147_964_211, true, "Good default for CPU", "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
            "The safe choice for a PC without a graphics card. Clear speech (news, lectures, tutorials) comes out well; accents, music and noisy scenes less so."),
        new("small.en-q5_1", "Small (English, quantized)", 190_098_681, true, "Accurate on CPU, fast on GPU", "bfdff4894dcb76bbf647d56263ea2a96645423f1669176f4844a1bf8e478ad30",
            "For a recent processor (6+ cores) or any graphics card. Clearly better than Base with accents and fast dialogue. On older processors subtitles may lag behind."),
        new("small.en", "Small (English)", 487_614_201, true, "Good default for GPU", "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
            "A good all-rounder with a graphics card. Slightly more accurate than the quantized Small; without a graphics card use that one, it is faster."),
        new("medium.en-q5_0", "Medium (English, quantized)", 539_225_533, true, "High accuracy, needs a GPU", "76733e26ad8fe1c7a5bf7531a9d41917b2adc0f20f2e4f5531688a8c6cd88eb0",
            "For a mid-range graphics card when Small still misses words. Too slow on a processor alone for live subtitles."),
        new("large-v3-turbo-q5_0", "Large v3 Turbo (quantized)", 574_041_195, false, "Best accuracy per cost, GPU", "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2",
            "Best for movies, series and hard audio if you have an NVIDIA or AMD graphics card (2 GB+ memory). Handles accents, background music and fast speech best."),
        new("large-v3-turbo", "Large v3 Turbo", 1_624_555_275, false, "Best accuracy, GPU with 4 GB+", "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69",
            "Highest accuracy, but only worth it on a strong graphics card with 4 GB+ memory. The quantized Turbo is almost as good at a third of the size."),
        new("small", "Small (multilingual)", 487_601_967, false, "Other source languages", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
            "Only when the speech is not English, or the spoken language is set to auto. For English, Small (English) is more accurate at the same size."),
    ];

    public static WhisperModel? Find(string id) => All.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Maps a preset to a model id for the backend that will actually run.</summary>
    public static string ModelForPreset(AsrPreset preset, bool gpu, string customModel) => (preset, gpu) switch
    {
        (AsrPreset.Custom, _) => customModel,
        (AsrPreset.Fast, false) => "base.en-q5_1",
        (AsrPreset.Balanced, false) => "base.en",
        (AsrPreset.Accurate, false) => "small.en-q5_1",
        (AsrPreset.Fast, true) => "base.en",
        (AsrPreset.Balanced, true) => "small.en",
        (AsrPreset.Accurate, true) => "large-v3-turbo-q5_0",
        _ => customModel,
    };

    /// <summary>Resolves Auto to Gpu only when a usable GPU and the Vulkan loader exist.</summary>
    public static AsrBackend ResolveBackend(AsrBackend requested, HardwareInfo hw, bool gpuCrashedLastRun)
    {
        if (gpuCrashedLastRun) return AsrBackend.Cpu;
        bool gpuUsable = hw.HasVulkan && hw.BestGpu is not null;
        return requested switch
        {
            AsrBackend.Cpu => AsrBackend.Cpu,
            AsrBackend.Gpu => hw.HasVulkan ? AsrBackend.Gpu : AsrBackend.Cpu,
            _ => gpuUsable ? AsrBackend.Gpu : AsrBackend.Cpu,
        };
    }

    /// <summary>
    /// faster-whisper (CTranslate2) model name for a catalog id. Quantization suffixes are dropped:
    /// CTranslate2 quantizes at load time through compute_type instead.
    /// </summary>
    public static string FasterWhisperModel(string id)
    {
        int dash = id.LastIndexOf('-');
        return dash > 0 && id[(dash + 1)..] is ['q', _, '_', ..] ? id[..dash] : id;
    }

    /// <summary>Whisper scales poorly past physical cores; leave one for the rest of the system.</summary>
    public static int DefaultThreads(HardwareInfo hw) => Math.Clamp(hw.PhysicalCores - (hw.PhysicalCores > 4 ? 1 : 0), 1, 8);
}
