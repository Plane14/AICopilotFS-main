using System;
using System.Windows;
using System.Windows.Threading;
using MSFS2024AIPilot.Core.Pilot;
using MSFS2024AIPilot.Core.State;

namespace MSFS2024AIPilot.Gui;

public partial class MainWindow : Window
{
    private PilotEngine? _engine;
    private readonly DispatcherTimer _telemetryTimer;

    public MainWindow()
    {
        InitializeComponent();
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _telemetryTimer.Tick += (_, _) => RefreshTelemetry();
        _telemetryTimer.Start();

        SldService.ValueChanged += (_, _) =>
        {
            TxtService.Text = $"{(int)SldService.Value} min";
            _engine?.SetServiceMinutes((int)SldService.Value);
        };

        Closed += (_, _) => _engine?.Dispose();
    }

    private PilotEngine Engine => _engine ??= CreateEngine();

    private PilotEngine CreateEngine()
    {
        var engine = new PilotEngine();
        engine.OnStatusChanged += status => Dispatcher.Invoke(() => ApplyStatus(status));
        engine.OnLog += msg => Dispatcher.Invoke(() => AppendLog(msg));
        return engine;
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        BtnConnect.IsEnabled = false;
        try
        {
            await Engine.ConnectAsync();
            TxtConnection.Text = "  ● connected";
            TxtConnection.Foreground = FindResource("GoodBrush") as System.Windows.Media.Brush;
            BtnConnect.Content = "Connected";
        }
        catch (Exception ex)
        {
            AppendLog($"[GUI] Connect failed: {ex.Message}");
            TxtError.Text = ex.Message;
        }
        finally { BtnConnect.IsEnabled = true; }
    }

    private void BtnEngage_Click(object sender, RoutedEventArgs e)
    {
        var mode = RbContinuous.IsChecked == true ? PilotMode.Continuous
                 : RbSingle.IsChecked == true ? PilotMode.SingleFlight
                 : PilotMode.Off;
        Engine.Engage(mode);
        TxtError.Text = "";
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e) => Engine.Pause();
    private void BtnDisengage_Click(object sender, RoutedEventArgs e) => Engine.Disengage();

    private async void BtnNextLeg_Click(object sender, RoutedEventArgs e)
    {
        BtnNextLeg.IsEnabled = false;
        try { await Engine.FlyNextLegAsync(); }
        finally { BtnNextLeg.IsEnabled = true; }
    }

    private async void BtnLoadPln_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MSFS flight plan (*.pln)|*.pln|All files (*.*)|*.*",
            Title = "Load flight plan",
        };
        if (dlg.ShowDialog() != true) return;
        BtnLoadPln.IsEnabled = false;
        try
        {
            var ok = await Engine.LoadFlightPlanAsync(dlg.FileName);
            TxtError.Text = ok ? "" : "Could not load flight plan";
            if (ok) RbSingle.IsChecked = true;
        }
        catch (Exception ex) { TxtError.Text = ex.Message; }
        finally { BtnLoadPln.IsEnabled = true; }
    }

    private void ApplyStatus(PilotStatus s)
    {
        TxtPhase.Text = s.Phase.ToString();
        TxtPhase.Foreground = s.Phase is FlightPhase.Takeoff or FlightPhase.Landing or FlightPhase.FinalApproach
            ? FindResource("WarnBrush") as System.Windows.Media.Brush
            : FindResource("AccentBrush") as System.Windows.Media.Brush;
        TxtStatus.Text = s.StatusText;
        TxtDistance.Text = $"{s.DistanceToGoNm:F0} nm";
        TxtEta.Text = $"{s.EtaMinutes:F0} min";
        TxtDep.Text = string.IsNullOrEmpty(s.DepartureIcao) ? "—" : $"{s.DepartureIcao} {s.DepartureRunway}";
        TxtDest.Text = string.IsNullOrEmpty(s.DestinationIcao) ? "—" : $"{s.DestinationIcao} {s.DestinationRunway}";
        TxtAircraft.Text = string.IsNullOrEmpty(s.AircraftTitle) ? "" : $"✈ {s.AircraftTitle}";
        TxtChecklistPhase.Text = s.ChecklistPhaseName;
        TxtChecklistItem.Text = s.CurrentChecklistItem;
        BarChecklist.Value = s.ChecklistProgress;
        ApplyTraffic(s);
        if (s.ServiceMinutesRemaining > 0)
            TxtStatus.Text = $"Servicing — {s.ServiceMinutesRemaining} min remaining";
    }

    private void RefreshTelemetry()
    {
        var s = _engine?.LatestState;
        if (s is null) return;
        TelIas.Text = $"{s.IndicatedAirspeed:F0} kt";
        TelAlt.Text = $"{s.Altitude:F0} ft";
        TelHdg.Text = $"{s.TrueHeading:F0}°";
        TelVs.Text = $"{s.VerticalSpeed:+0;-0;0} fpm";
        TelGs.Text = $"{s.GroundSpeed:F0} kt";
        TelFuel.Text = s.TotalFuelQuantity > 0 ? $"{s.TotalFuelQuantity:F0} gal" : "—";
        TelWind.Text = $"{s.WindVelocity:F0} kt {Compass(s.WindDirection)}";
        TelOat.Text = $"{s.OAT:F0}°C";
        TelGear.Text = s.GearDown ? "DOWN" : "UP";
        TelFlaps.Text = $"{s.FlapsHandleIndex:F0}";
        TelAp.Text = s.Autopilot.MasterSwitch ? "ON" : "off";
        TelIce.Text = s.StructuralIcePercent > 1 ? $"{s.StructuralIcePercent:F0}%" : "—";
    }

    private void ApplyTraffic(PilotStatus s)
    {
        TelTraffic.Text = s.TrafficContacts > 0 ? s.TrafficSummary : "—";
        TelTraffic.Foreground = s.TrafficSummary.Contains("occupied") || s.TrafficSummary.Contains("conflict")
            ? FindResource("WarnBrush") as System.Windows.Media.Brush
            : FindResource("DimBrush") as System.Windows.Media.Brush;
    }

    private static string Compass(double deg)
    {
        var dirs = new[] { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
        return dirs[(int)Math.Round(GeoNormalize(deg) / 22.5) % 16];
    }

    private static double GeoNormalize(double d) => ((d % 360) + 360) % 360;

    private void AppendLog(string msg)
    {
        LstLog.Items.Add(msg);
        while (LstLog.Items.Count > 500) LstLog.Items.RemoveAt(0);
        LstLog.ScrollIntoView(LstLog.Items[^1]);
    }
}
