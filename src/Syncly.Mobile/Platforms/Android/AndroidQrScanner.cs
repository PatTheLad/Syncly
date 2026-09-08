#pragma warning disable CS0618 // Camera API is deprecated but still the simplest live preview on API 26+.
using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware;
using Android.OS;
using Android.Views;
using Android.Widget;
using Syncly.App;
using ZXing;
using ZXing.Common;
using Camera = Android.Hardware.Camera;
using Permission = Android.Manifest.Permission;

namespace Syncly.Mobile;

internal sealed class MauiCameraPermission : IDeviceCamera
{
    public async Task<bool> EnsurePermissionAsync(CancellationToken ct = default)
    {
        _ = ct;
        var status = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Camera>();
        return status == PermissionStatus.Granted;
    }
}

internal sealed class AndroidQrScanner : IQrScanner
{
    public async Task<string?> ScanAsync(CancellationToken ct = default)
    {
        var activity = Platform.CurrentActivity
                       ?? throw new InvalidOperationException("Syncly is not in the foreground.");

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

        activity.RunOnUiThread(() =>
        {
            QrScanActivity.Completion = tcs;
            activity.StartActivity(new Android.Content.Intent(activity, typeof(QrScanActivity)));
        });

        try
        {
            return await tcs.Task;
        }
        finally
        {
            if (ReferenceEquals(QrScanActivity.Completion, tcs))
                QrScanActivity.Completion = null;
        }
    }
}

[Activity(
    Label = "Scan QR",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ScreenOrientation = ScreenOrientation.Portrait,
    Exported = false)]
public sealed class QrScanActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    internal static TaskCompletionSource<string?>? Completion;

    private SurfaceView? _preview;
    private Camera? _camera;
    private bool _finished;
    private readonly BarcodeReaderGeneric _reader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            PossibleFormats = [BarcodeFormat.QR_CODE],
            TryHarder = true,
        },
    };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (CheckSelfPermission(Permission.Camera) != Permission.Granted)
        {
            FinishWith(null);
            return;
        }

        var root = new FrameLayout(this);
        _preview = new SurfaceView(this);
        _preview.Holder?.AddCallback(this);

        var cancel = new Button(this) { Text = "Cancel" };
        cancel.Click += (_, _) => FinishWith(null);

        var hint = new TextView(this)
        {
            Text = "Point at the Syncly invite QR",
            Gravity = GravityFlags.Center,
        };
        hint.SetTextColor(Color.White);
        hint.SetBackgroundColor(Color.Argb(160, 0, 0, 0));
        hint.SetPadding(24, 16, 24, 16);

        var overlay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        overlay.SetGravity(GravityFlags.Bottom | GravityFlags.CenterHorizontal);
        overlay.SetPadding(32, 32, 32, 48);
        overlay.AddView(hint, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent));
        overlay.AddView(cancel, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent,
            ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.CenterHorizontal,
            TopMargin = 24,
        });

        root.AddView(_preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));
        root.AddView(overlay, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Bottom,
        });

        SetContentView(root);
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        try
        {
            _camera = Camera.Open(0);
            _camera.SetDisplayOrientation(90);
            _camera.SetPreviewDisplay(holder);
            var parameters = _camera.GetParameters();
            parameters?.SetPreviewFormat(ImageFormatType.Nv21);
            if (parameters?.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true)
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            _camera.SetParameters(parameters);
            _camera.SetPreviewCallback(this);
            _camera.StartPreview();
        }
        catch (Exception)
        {
            FinishWith(null);
        }
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }

    public void SurfaceDestroyed(ISurfaceHolder holder) => ReleaseCamera();

    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        if (_finished || data is null || camera is null)
            return;

        Camera.Size? size;
        try
        {
            size = camera.GetParameters()?.PreviewSize;
        }
        catch
        {
            return;
        }

        if (size is null)
            return;

        try
        {
            var source = new PlanarYUVLuminanceSource(
                data, size.Width, size.Height, 0, 0, size.Width, size.Height, false);
            var result = _reader.Decode(source);
            if (result?.Text is { Length: > 0 } text)
                RunOnUiThread(() => FinishWith(text));
        }
        catch (Exception)
        {
            // Keep scanning.
        }
    }

    protected override void OnDestroy()
    {
        ReleaseCamera();
        base.OnDestroy();
        if (!_finished)
            Completion?.TrySetResult(null);
    }

    private void ReleaseCamera()
    {
        try
        {
            _camera?.SetPreviewCallback(null);
            _camera?.StopPreview();
            _camera?.Release();
        }
        catch
        {
            // ignored
        }

        _camera = null;
    }

    private void FinishWith(string? value)
    {
        if (_finished)
            return;
        _finished = true;
        ReleaseCamera();
        Completion?.TrySetResult(value);
        Finish();
    }
}
