using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kyvoq.App.Services;
using Kyvoq.App.ViewModels;
using Kyvoq.Core.Models;

namespace Kyvoq.App.Tests.ViewModels;

/// <summary>
/// 验证虚拟化网格使用的项目视图模型不会提前加载不可见图标。
/// </summary>
public sealed class LauncherItemViewModelTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        "Kyvoq.App.Tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 删除测试生成的图标与缓存目录。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(temporaryDirectory))
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    /// <summary>
    /// 验证构造项目不会产生磁盘缓存，首次读取 Icon 后才开始异步加载。
    /// </summary>
    [Fact]
    public async Task Icon_ShouldLoadOnlyAfterFirstRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(temporaryDirectory);
        var sourcePath = Path.Combine(temporaryDirectory, "source.png");
        var cachePath = Path.Combine(temporaryDirectory, "cache");
        WritePng(sourcePath);
        var viewModel = new LauncherItemViewModel(
            new LauncherItem
            {
                Name = "延迟图标",
                Target = @"C:\Missing.exe",
                CustomIconPath = sourcePath
            },
            new IconCacheService(cachePath));
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += HandlePropertyChanged;

        Assert.False(Directory.Exists(cachePath));
        _ = viewModel.Icon;
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.NotNull(viewModel.Icon);
        Assert.Single(Directory.EnumerateFiles(cachePath, "*.png"));
        return;

        /// <summary>
        /// 在图标属性完成异步更新时结束测试等待。
        /// </summary>
        /// <param name="sender">项目视图模型。</param>
        /// <param name="eventArgs">属性变化参数。</param>
        void HandlePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName == nameof(LauncherItemViewModel.Icon))
            {
                loaded.TrySetResult();
            }
        }
    }

    /// <summary>
    /// 验证项目显示名称在分隔符、驼峰边界和字母数字边界添加不可见软换行机会。
    /// </summary>
    /// <param name="name">原始项目名称。</param>
    /// <param name="expected">包含零宽空格的预期显示名称。</param>
    [Theory]
    [InlineData("Visual Studio Code", "Visual Studio Code")]
    [InlineData("Moba_Text-Editor", "Moba_\u200BText-\u200BEditor")]
    [InlineData("MobaTextEditor", "Moba\u200BText\u200BEditor")]
    [InlineData("XMLHttpRequest", "XML\u200BHttp\u200BRequest")]
    [InlineData("VisualStudio2022", "Visual\u200BStudio\u200B2022")]
    public void CreateDisplayName_ShouldAddMatureSoftWrapOpportunities(
        string name,
        string expected)
    {
        var displayName = LauncherItemViewModel.CreateDisplayName(name);

        Assert.Equal(expected, displayName);
    }

    /// <summary>
    /// 创建用于触发图标读取的最小 PNG。
    /// </summary>
    /// <param name="path">图片输出路径。</param>
    private static void WritePng(string path)
    {
        var pixels = new byte[]
        {
            80, 120, 240, 255,
            80, 120, 240, 255,
            80, 120, 240, 255,
            80, 120, 240, 255
        };
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }
}
