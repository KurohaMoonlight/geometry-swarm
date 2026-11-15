using System;
using System.Collections.Generic;
using GeometrySwarm.Core.ECS.Components;
using GeometrySwarm.Gameplay.Enemy;
using Godot;

namespace GeometrySwarm.Gameplay;

/// <summary>
/// Sistem Peta Taktis & Radar Kosmik (Tactical Map & Mini-Map System)
/// - Menampilkan peta ruang angkasa luas (Sektor 12.000 x 12.000 px).
/// - Sabuk kosmik terpetakan: Void Core Horizon (1.550 px), Inner Belt (1.950 - 2.450 px),
///   Mid Belt / Main Belt (2.600 - 4.250 px), & Outer Belt / Kuiper Reach (4.450 - 5.500 px).
/// - Posisi pemain saat ini (Player Core) dengan heading arah gerak, sonar beacon, & telemetri koordinat.
/// - Menampilkan asteroid di dekat pemain dalam radius 2 kali viewport (sisanya tertutup War Fog).
/// - Black Hole (Cosmic Rift) memiliki gravitasi masif sehingga sinyalnya MENEMBUS WAR FOG
///   dan selalu terlihat dari seluruh penjuru sektor.
/// - Dapat dibuka/ditutup kapan saja dengan menekan tombol [M] atau mengklik Radar di HUD.
/// </summary>
public partial class TacticalMap : Control
{
    public static TacticalMap? Instance { get; private set; }

    // Status Map
    private bool _isTacticalMapOpen = false;
    public bool IsTacticalMapOpen => _isTacticalMapOpen;

    // Sub-komponen UI
    private PanelContainer? _miniMapContainer;
    private Control? _miniMapCanvas;
    private Button? _miniMapExpandButton;

    private Control? _tacticalMapOverlay;
    private Control? _tacticalMapCanvas;
    private Label? _telemetryLabel;
    private Button? _closeMapButton;

    // Timer Animasi (Sweep radar, Sonar pulse, Singularity swirl)
    private float _animTimer = 0.0f;
    private float _pulseTimer = 0.0f;
    private float _miniMapRedrawTimer = 0.0f;
    private float _telemetryUpdateTimer = 0.0f;

    // Zero-allocation reusable buffers untuk DrawPolygon (Menghemat puluhan ribu alokasi array/detik)
    private static readonly Vector2[] _reusableQuad = new Vector2[4];
    private static readonly Color[] _reusableQuadColors = new Color[4];

    private static void DrawQuadPolygon(Control canvas, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color c0, Color c1, Color c2, Color c3)
    {
        _reusableQuad[0] = p0;
        _reusableQuad[1] = p1;
        _reusableQuad[2] = p2;
        _reusableQuad[3] = p3;
        _reusableQuadColors[0] = c0;
        _reusableQuadColors[1] = c1;
        _reusableQuadColors[2] = c2;
        _reusableQuadColors[3] = c3;
        canvas.DrawPolygon(_reusableQuad, _reusableQuadColors);
    }

    // Konstanta Ukuran Sektor Dunia
    public const float WorldSectorRadius = 6000.0f; // Sektor 12.000 x 12.000 px (Pusat di 0,0)
    public const float VoidCoreRadius = 1550.0f;
    public const float InnerBeltMin = 1950.0f;
    public const float InnerBeltMax = 2450.0f;
    public const float MidBeltMin = 2600.0f;
    public const float MidBeltMax = 4250.0f;
    public const float OuterBeltMin = 4450.0f;
    public const float OuterBeltMax = 5500.0f;

    // Palet Warna Cyberpunk Sci-Fi
    private static readonly Color BgColor = new(0.014f, 0.016f, 0.028f, 0.94f);
    private static readonly Color GridColor = new(0.12f, 0.22f, 0.35f, 0.22f);
    private static readonly Color AxisColor = new(0.25f, 0.45f, 0.65f, 0.38f);
    private static readonly Color PerimeterColor = new(0.40f, 0.20f, 0.65f, 0.55f);

    private static readonly Color VoidHorizonColor = new(0.95f, 0.25f, 0.85f, 0.60f);
    private static readonly Color InnerBeltColor = new(0.20f, 0.65f, 0.95f, 0.08f);
    private static readonly Color InnerBeltRingColor = new(0.30f, 0.75f, 1.0f, 0.32f);
    private static readonly Color MidBeltColor = new(0.95f, 0.75f, 0.22f, 0.07f);
    private static readonly Color MidBeltRingColor = new(1.0f, 0.82f, 0.30f, 0.28f);
    private static readonly Color OuterBeltColor = new(0.65f, 0.30f, 0.95f, 0.06f);
    private static readonly Color OuterBeltRingColor = new(0.75f, 0.42f, 1.0f, 0.24f);

    private static readonly Color WarFogColor = new(0.008f, 0.006f, 0.015f, 0.88f);
    private static readonly Color SensorRingColor = new(0.25f, 0.90f, 1.0f, 0.85f);
    private static readonly Color PlayerMarkerColor = new(0.15f, 1.0f, 0.88f, 1.0f);
    private static readonly Color BlackHoleColor = new(1.8f, 0.35f, 2.2f, 1.0f);
    private static readonly Color RivalThreatColor = new(2.2f, 0.25f, 0.45f, 1.0f);

    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        LayoutMode = 3;
        AnchorsPreset = (int)LayoutPreset.FullRect;
        AnchorRight = 1.0f;
        AnchorBottom = 1.0f;

        BuildMiniMapWidget();
        BuildTacticalMapOverlay();
    }

    /// <summary>
    /// Membangun Mini-Map Radar di pojok kanan bawah HUD.
    /// </summary>
    private void BuildMiniMapWidget()
    {
        var miniContainer = new PanelContainer
        {
            Name = "MiniMapPanel",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.BottomRight,
            AnchorLeft = 1.0f,
            AnchorRight = 1.0f,
            AnchorTop = 1.0f,
            AnchorBottom = 1.0f,
            OffsetLeft = -236,
            OffsetTop = -254,
            OffsetRight = -16,
            OffsetBottom = -16,
            CustomMinimumSize = new Vector2(220, 238),
            MouseFilter = MouseFilterEnum.Stop
        };

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.02f, 0.025f, 0.045f, 0.86f),
            BorderColor = new Color(0.25f, 0.65f, 0.95f, 0.55f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        miniContainer.AddThemeStyleboxOverride("panel", panelStyle);

        var vbox = new VBoxContainer();
        vbox.AddThemeConstantOverride("separation", 4);
        miniContainer.AddChild(vbox);

        // Header Radar
        var headerHBox = new HBoxContainer();
        var titleLabel = new Label
        {
            Text = "⬡ RADAR TAKTIS",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.90f, 1.0f));
        titleLabel.AddThemeFontSizeOverride("font_size", 11);
        headerHBox.AddChild(titleLabel);

        var keyBadge = new Label
        {
            Text = "[M]"
        };
        keyBadge.AddThemeColorOverride("font_color", new Color(1.0f, 0.85f, 0.35f));
        keyBadge.AddThemeFontSizeOverride("font_size", 10);
        headerHBox.AddChild(keyBadge);
        vbox.AddChild(headerHBox);

        // Canvas Gambar Radar
        _miniMapCanvas = new Control
        {
            Name = "MiniMapCanvas",
            CustomMinimumSize = new Vector2(204, 178),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Pass
        };
        _miniMapCanvas.Draw += DrawMiniMapCanvas;
        _miniMapCanvas.GuiInput += (InputEvent ev) =>
        {
            if (ev is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                ToggleTacticalMap();
                GetViewport().SetInputAsHandled();
            }
        };
        vbox.AddChild(_miniMapCanvas);

        // Tombol Perbesar Peta
        _miniMapExpandButton = new Button
        {
            Text = "▲ BUKA PETA LENGKAP [M]",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(0, 24)
        };
        _miniMapExpandButton.AddThemeFontSizeOverride("font_size", 10);
        _miniMapExpandButton.AddThemeColorOverride("font_color", new Color(0.75f, 0.90f, 1.0f));
        _miniMapExpandButton.Pressed += ToggleTacticalMap;
        vbox.AddChild(_miniMapExpandButton);

        _miniMapContainer = miniContainer;
        AddChild(_miniMapContainer);
    }

    /// <summary>
    /// Membangun Modal Overlay Peta Taktis Lengkap (Full Tactical Map Modal)
    /// </summary>
    private void BuildTacticalMapOverlay()
    {
        _tacticalMapOverlay = new Control
        {
            Name = "TacticalMapOverlay",
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop
        };

        // Latar Belakang Semi-Transparan Hitam Luar Angkasa
        var backdrop = new ColorRect
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            Color = new Color(0.010f, 0.012f, 0.020f, 0.90f),
            MouseFilter = MouseFilterEnum.Stop
        };
        _tacticalMapOverlay.AddChild(backdrop);

        // Kontainer Utama di Tengah
        var centerContainer = new CenterContainer
        {
            LayoutMode = 1,
            AnchorsPreset = (int)LayoutPreset.FullRect,
            AnchorRight = 1.0f,
            AnchorBottom = 1.0f,
            MouseFilter = MouseFilterEnum.Pass
        };
        _tacticalMapOverlay.AddChild(centerContainer);

        var modalPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(880, 920),
            MouseFilter = MouseFilterEnum.Stop
        };
        var modalStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.018f, 0.022f, 0.038f, 0.98f),
            BorderColor = new Color(0.35f, 0.75f, 1.0f, 0.85f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 12,
            CornerRadiusTopRight = 12,
            CornerRadiusBottomLeft = 12,
            CornerRadiusBottomRight = 12,
            ContentMarginLeft = 20,
            ContentMarginRight = 20,
            ContentMarginTop = 16,
            ContentMarginBottom = 16,
            ShadowColor = new Color(0.1f, 0.5f, 0.9f, 0.25f),
            ShadowSize = 28
        };
        modalPanel.AddThemeStyleboxOverride("panel", modalStyle);
        centerContainer.AddChild(modalPanel);

        var mainVBox = new VBoxContainer();
        mainVBox.AddThemeConstantOverride("separation", 10);
        modalPanel.AddChild(mainVBox);

        // 1. Header Bar
        var headerHBox = new HBoxContainer();
        var titleVBox = new VBoxContainer();
        titleVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var titleLabel = new Label
        {
            Text = "⬡ TACTICAL SECTOR MAP // NAVIGASI KOSMIK"
        };
        titleLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.95f, 1.0f));
        titleLabel.AddThemeFontSizeOverride("font_size", 16);
        titleVBox.AddChild(titleLabel);

        var subtitleLabel = new Label
        {
            Text = "SEKTOR: 12.000 × 12.000 px  •  RADAR SCAN: 2x VIEWPORT  •  WAR FOG AKTIF"
        };
        subtitleLabel.AddThemeColorOverride("font_color", new Color(0.65f, 0.80f, 0.95f, 0.75f));
        subtitleLabel.AddThemeFontSizeOverride("font_size", 11);
        titleVBox.AddChild(subtitleLabel);
        headerHBox.AddChild(titleVBox);

        _closeMapButton = new Button
        {
            Text = "✕ TUTUP [M / ESC]",
            FocusMode = FocusModeEnum.None,
            CustomMinimumSize = new Vector2(140, 32)
        };
        _closeMapButton.AddThemeFontSizeOverride("font_size", 11);
        _closeMapButton.AddThemeColorOverride("font_color", new Color(1.0f, 0.45f, 0.45f));
        _closeMapButton.Pressed += CloseTacticalMap;
        headerHBox.AddChild(_closeMapButton);
        mainVBox.AddChild(headerHBox);

        var sep1 = new HSeparator();
        sep1.AddThemeColorOverride("separator_color", new Color(0.25f, 0.45f, 0.65f, 0.45f));
        mainVBox.AddChild(sep1);

        // 2. Canvas Peta Taktis Utama
        _tacticalMapCanvas = new Control
        {
            Name = "TacticalMapCanvas",
            CustomMinimumSize = new Vector2(840, 720),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Pass
        };
        _tacticalMapCanvas.Draw += DrawTacticalMapCanvas;
        mainVBox.AddChild(_tacticalMapCanvas);

        var sep2 = new HSeparator();
        sep2.AddThemeColorOverride("separator_color", new Color(0.25f, 0.45f, 0.65f, 0.45f));
        mainVBox.AddChild(sep2);

        // 3. Footer Bar: Legenda & Telemetri Real-time
        var footerHBox = new HBoxContainer();
        footerHBox.AddThemeConstantOverride("separation", 16);

        // Legenda Sabuk & Elemen
        var legendVBox = new VBoxContainer();
        legendVBox.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var beltLegendLabel = new Label
        {
            Text = "SABUK:  ● Void Core (1.55K)   ● Inner Belt (1.95K-2.45K)   ● Mid Belt (2.6K-4.25K)   ● Outer Belt (4.45K-5.5K)"
        };
        beltLegendLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.88f, 0.98f, 0.9f));
        beltLegendLabel.AddThemeFontSizeOverride("font_size", 11);
        legendVBox.AddChild(beltLegendLabel);

        var resLegendLabel = new Label
        {
            Text = "OBJEK:  ◆ Fe (Ferrite)  ◆ Qz (Quartz)  ◆ Cb (Carbide)  ◆ Vx (Vortexum)  ★ Bintang Aether  |  ⬡ Black Hole (Sinyal Tembus Warfog)"
        };
        resLegendLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.45f, 0.85f));
        resLegendLabel.AddThemeFontSizeOverride("font_size", 10);
        legendVBox.AddChild(resLegendLabel);
        footerHBox.AddChild(legendVBox);

        // Telemetri Koordinat
        _telemetryLabel = new Label
        {
            Text = "POSISI: [0, 0] | KECEPATAN: 0 px/s | SENSOR: 0 Asteroid",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _telemetryLabel.AddThemeColorOverride("font_color", new Color(0.35f, 0.95f, 0.85f));
        _telemetryLabel.AddThemeFontSizeOverride("font_size", 11);
        footerHBox.AddChild(_telemetryLabel);

        mainVBox.AddChild(footerHBox);

        AddChild(_tacticalMapOverlay);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        _animTimer += dt;
        _pulseTimer = Mathf.Wrap(_pulseTimer + dt * 1.8f, 0.0f, 1.0f);

        // Throttle redraw Mini-Map radar ke 24 FPS (Hemat siklus CPU, GPU, & GC)
        _miniMapRedrawTimer += dt;
        if (_miniMapRedrawTimer >= 0.042f)
        {
            _miniMapRedrawTimer = 0.0f;
            if (_miniMapCanvas != null && _miniMapCanvas.IsVisibleInTree())
            {
                _miniMapCanvas.QueueRedraw();
            }
        }

        // Redraw Peta Taktis Lengkap saat sedang dibuka (Throttle string telemetri ke ~6.6 FPS)
        if (_isTacticalMapOpen && _tacticalMapCanvas != null)
        {
            _telemetryUpdateTimer += dt;
            if (_telemetryUpdateTimer >= 0.15f)
            {
                _telemetryUpdateTimer = 0.0f;
                UpdateTelemetryLabel();
            }
            _tacticalMapCanvas.QueueRedraw();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
        {
            if (keyEvent.Keycode == Key.M)
            {
                ToggleTacticalMap();
                GetViewport().SetInputAsHandled();
            }
            else if (keyEvent.Keycode == Key.Escape && _isTacticalMapOpen)
            {
                CloseTacticalMap();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public void ToggleTacticalMap()
    {
        if (_isTacticalMapOpen)
        {
            CloseTacticalMap();
        }
        else
        {
            OpenTacticalMap();
        }
    }

    public void OpenTacticalMap()
    {
        _isTacticalMapOpen = true;
        if (_tacticalMapOverlay != null)
        {
            _tacticalMapOverlay.Visible = true;
        }
    }

    public void CloseTacticalMap()
    {
        _isTacticalMapOpen = false;
        if (_tacticalMapOverlay != null)
        {
            _tacticalMapOverlay.Visible = false;
        }
    }

    /// <summary>
    /// Menghitung radius jangkauan sensor aktif (2 kali viewport) dalam koordinat dunia.
    /// Memperhitungkan ukuran resolusi layar dan level zoom kamera aktif.
    /// </summary>
    public float GetVisionRadiusWorld()
    {
        Vector2 vpSize = GetViewportRect().Size;
        float baseDiagonal = vpSize.Length();
        float currentZoom = PlayerCamera.Instance != null
            ? Mathf.Clamp(PlayerCamera.Instance.Zoom.X, 0.35f, 1.0f)
            : 0.88f;

        // 2 kali ukuran viewport dunia (diameter ~2x layar, radius ~1x diagonal dunia)
        return (baseDiagonal / currentZoom) * 1.05f;
    }

    private void UpdateTelemetryLabel()
    {
        if (_telemetryLabel == null) return;

        var player = PlayerCore.Instance;
        Vector2 pPos = player?.GlobalPosition ?? Vector2.Zero;
        Vector2 pVel = player?.Velocity ?? Vector2.Zero;
        float speed = pVel.Length();

        float visionRad = GetVisionRadiusWorld();
        int asteroidsInScan = 0;
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast != null && IsInstanceValid(ast) && ast.IsActiveInTree)
            {
                if (ast.CachedPosition.DistanceTo(pPos) <= visionRad)
                {
                    asteroidsInScan++;
                }
            }
        }

        // Cari Black Hole terdekat
        string nearestRiftInfo = "-";
        float minRiftDist = float.MaxValue;
        var rifts = AsteroidFieldManager.Instance?.CosmicRifts;
        if (rifts != null)
        {
            for (int r = 0; r < rifts.Count; r++)
            {
                var rift = rifts[r];
                if (rift != null && IsInstanceValid(rift))
                {
                    float d = rift.GlobalPosition.DistanceTo(pPos);
                    if (d < minRiftDist)
                    {
                        minRiftDist = d;
                        nearestRiftInfo = $"BH-{rift.RiftId + 1} ({d:0} px)";
                    }
                }
            }
        }

        _telemetryLabel.Text = $"POSISI: [X: {pPos.X:0}, Y: {pPos.Y:0}]  |  SPD: {speed:0} px/s  |  SENSOR: {asteroidsInScan} Asteroid  |  BH TERDEKAT: {nearestRiftInfo}";
    }

    // =========================================================================
    // RENDERING PETA TAKTIS LENGKAP (TACTICAL MAP CANVAS)
    // =========================================================================
    private void DrawTacticalMapCanvas()
    {
        if (_tacticalMapCanvas == null) return;

        Vector2 canvasSize = _tacticalMapCanvas.Size;
        Vector2 center = canvasSize * 0.5f;

        // Skala koordinat dunia (-6000 s/d +6000) ke kanvas
        float mapSize = MathF.Min(canvasSize.X, canvasSize.Y) - 32.0f;
        float worldToMapScale = mapSize / (WorldSectorRadius * 2.0f);

        Vector2 WorldToMap(Vector2 worldPos) => center + worldPos * worldToMapScale;
        float WorldDistToMap(float worldDist) => worldDist * worldToMapScale;

        // 1. Latar Belakang & Grid Ruang Angkasa
        _tacticalMapCanvas.DrawRect(new Rect2(Vector2.Zero, canvasSize), BgColor, true);

        // Garis Grid Taktis Koordinat
        for (float g = -4500.0f; g <= 4500.0f; g += 1500.0f)
        {
            if (MathF.Abs(g) < 1.0f) continue;
            Vector2 pX1 = WorldToMap(new Vector2(g, -WorldSectorRadius));
            Vector2 pX2 = WorldToMap(new Vector2(g, WorldSectorRadius));
            _tacticalMapCanvas.DrawLine(pX1, pX2, GridColor, 1.0f);

            Vector2 pY1 = WorldToMap(new Vector2(-WorldSectorRadius, g));
            Vector2 pY2 = WorldToMap(new Vector2(WorldSectorRadius, g));
            _tacticalMapCanvas.DrawLine(pY1, pY2, GridColor, 1.0f);
        }

        // Garis Sumbu Utama X & Y (Origin Crosshair)
        _tacticalMapCanvas.DrawLine(WorldToMap(new Vector2(-WorldSectorRadius, 0)), WorldToMap(new Vector2(WorldSectorRadius, 0)), AxisColor, 1.2f);
        _tacticalMapCanvas.DrawLine(WorldToMap(new Vector2(0, -WorldSectorRadius)), WorldToMap(new Vector2(0, WorldSectorRadius)), AxisColor, 1.2f);

        // Batas Terluar Sektor (12.000 x 12.000 px)
        Vector2 sectorTopLeft = WorldToMap(new Vector2(-WorldSectorRadius, -WorldSectorRadius));
        Vector2 sectorBottomRight = WorldToMap(new Vector2(WorldSectorRadius, WorldSectorRadius));
        Rect2 sectorRect = new Rect2(sectorTopLeft, sectorBottomRight - sectorTopLeft);
        _tacticalMapCanvas.DrawRect(sectorRect, PerimeterColor, false, 1.5f);

        // 2. Menggambar Sabuk-Sabuk Kosmik (Void Core, Inner, Mid, Outer Belts)
        DrawConcentricBelt(_tacticalMapCanvas, center, WorldDistToMap(VoidCoreRadius), 0.0f, VoidHorizonColor, true, "VOID HORIZON (1.55K)");
        DrawConcentricBeltBand(_tacticalMapCanvas, center, WorldDistToMap(InnerBeltMin), WorldDistToMap(InnerBeltMax), InnerBeltColor, InnerBeltRingColor, "▲ INNER BELT (1.95K - 2.45K)");
        DrawConcentricBeltBand(_tacticalMapCanvas, center, WorldDistToMap(MidBeltMin), WorldDistToMap(MidBeltMax), MidBeltColor, MidBeltRingColor, "▲ MID BELT // MAIN ASTEROID BELT (2.6K - 4.25K)");
        DrawConcentricBeltBand(_tacticalMapCanvas, center, WorldDistToMap(OuterBeltMin), WorldDistToMap(OuterBeltMax), OuterBeltColor, OuterBeltRingColor, "▲ OUTER BELT // KUIPER REACH (4.45K - 5.5K)");

        // Posisi Pemain & Radius Penglihatan Sensor (2x Viewport)
        var player = PlayerCore.Instance;
        Vector2 playerWorldPos = player?.GlobalPosition ?? Vector2.Zero;
        Vector2 playerMapPos = WorldToMap(playerWorldPos);
        float visionRadiusWorld = GetVisionRadiusWorld();
        float visionRadiusMap = WorldDistToMap(visionRadiusWorld);

        // 3. Menggambar Asteroid DALAM JANGKAUAN 2x VIEWPORT (Sebelum Fog Overlay)
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree) continue;

            float distToPlayer = ast.CachedPosition.DistanceTo(playerWorldPos);

            // Asteroid di luar 2x viewport tertutup WAR FOG (tidak digambar sama sekali)
            if (distToPlayer > visionRadiusWorld) continue;

            Vector2 astMapPos = WorldToMap(ast.CachedPosition);
            float astMapRad = MathF.Max(2.5f, WorldDistToMap(ast.Radius));

            if (ast.IsEnergyAsteroid)
            {
                // Bintang Aether (Radiant Solar Amber Diamond)
                DrawAetherStarBlip(_tacticalMapCanvas, astMapPos, astMapRad);
            }
            else
            {
                // Asteroid Mineral Batu (Warna sesuai Resource)
                Color resColor = GetResourceColor(ast.PrimaryResource);
                _tacticalMapCanvas.DrawCircle(astMapPos, astMapRad, resColor);
                _tacticalMapCanvas.DrawArc(astMapPos, astMapRad + 0.5f, 0, Mathf.Tau, 12, resColor.Lightened(0.35f), 1.0f);
            }
        }

        // 4. MENGGAMBAR WAR FOG (Menutupi seluruh sektor KECUALI lingkaran 2x viewport di sekitar pemain)
        DrawWarFogCover(_tacticalMapCanvas, canvasSize, playerMapPos, visionRadiusMap);

        // Cincin Batas Sensor Aktif (Sensor Perimeter Indicator)
        _tacticalMapCanvas.DrawArc(playerMapPos, visionRadiusMap, 0, Mathf.Tau, 64, SensorRingColor, 1.8f);
        _tacticalMapCanvas.DrawArc(playerMapPos, visionRadiusMap * 0.98f, 0, Mathf.Tau, 48, new Color(0.2f, 0.8f, 1.0f, 0.35f), 1.0f);

        // Garis Sweep Radar yang berputar halus
        float sweepAngle = Mathf.Wrap(_animTimer * 1.35f, 0.0f, Mathf.Tau);
        Vector2 sweepEnd = playerMapPos + new Vector2(MathF.Cos(sweepAngle), MathF.Sin(sweepAngle)) * visionRadiusMap;
        _tacticalMapCanvas.DrawLine(playerMapPos, sweepEnd, new Color(0.35f, 0.95f, 1.0f, 0.65f), 1.5f);

        // Label Sensor di Cincin
        Vector2 sensorLabelPos = playerMapPos + new Vector2(0, -visionRadiusMap - 6.0f);
        _tacticalMapCanvas.DrawString(ThemeDB.FallbackFont, sensorLabelPos, "SENSOR HORIZON [2x VIEWPORT]", HorizontalAlignment.Center, -1, 10, new Color(0.35f, 0.95f, 1.0f, 0.85f));

        // 5. MENGGAMBAR BLACK HOLE (COSMIC RIFT) - SELALU TERLIHAT MESKI DI DALAM WAR FOG!
        var rifts = AsteroidFieldManager.Instance?.CosmicRifts;
        if (rifts != null)
        {
            for (int r = 0; r < rifts.Count; r++)
            {
                var rift = rifts[r];
                if (rift == null || !IsInstanceValid(rift)) continue;

                Vector2 riftMapPos = WorldToMap(rift.GlobalPosition);
                DrawCosmicRiftMarker(_tacticalMapCanvas, riftMapPos, rift, playerWorldPos);
            }
        }

        // 6. Menggambar Rival Hive Core (Jika berada dalam jangkauan sensor)
        var enemyCore = SwarmSimulation.Instance?.EnemyCore;
        if (enemyCore != null && IsInstanceValid(enemyCore) && enemyCore.Health > 0.0f)
        {
            float distToEnemy = enemyCore.GlobalPosition.DistanceTo(playerWorldPos);
            if (distToEnemy <= visionRadiusWorld)
            {
                Vector2 enemyMapPos = WorldToMap(enemyCore.GlobalPosition);
                DrawRivalCoreMarker(_tacticalMapCanvas, enemyMapPos, enemyCore);
            }
        }

        // 7. Menggambar Posisi Pemain (Player Core Marker)
        DrawPlayerCoreMarker(_tacticalMapCanvas, playerMapPos, player);
    }

    // =========================================================================
    // RENDERING MINI-MAP WIDGET DI HUD (CORNER RADAR)
    // =========================================================================
    private void DrawMiniMapCanvas()
    {
        if (_miniMapCanvas == null) return;

        Vector2 canvasSize = _miniMapCanvas.Size;
        Vector2 center = canvasSize * 0.5f;
        float radius = MathF.Min(center.X, center.Y) - 4.0f;

        // Skala Radar Mini-Map
        float miniScale = radius / (WorldSectorRadius * 1.05f);
        Vector2 WorldToMini(Vector2 worldPos) => center + worldPos * miniScale;
        float WorldDistToMini(float worldDist) => worldDist * miniScale;

        // Latar Belakang Lingkaran Radar
        _miniMapCanvas.DrawCircle(center, radius, new Color(0.015f, 0.018f, 0.032f, 0.95f));
        _miniMapCanvas.DrawArc(center, radius, 0, Mathf.Tau, 48, new Color(0.3f, 0.7f, 1.0f, 0.5f), 1.5f);

        // Sumbu Salib Tipis
        _miniMapCanvas.DrawLine(new Vector2(center.X - radius, center.Y), new Vector2(center.X + radius, center.Y), new Color(0.2f, 0.4f, 0.6f, 0.25f), 1.0f);
        _miniMapCanvas.DrawLine(new Vector2(center.X, center.Y - radius), new Vector2(center.X, center.Y + radius), new Color(0.2f, 0.4f, 0.6f, 0.25f), 1.0f);

        // Cincin Sabuk Tipis
        _miniMapCanvas.DrawArc(center, WorldDistToMini(VoidCoreRadius), 0, Mathf.Tau, 32, VoidHorizonColor * 0.7f, 1.0f);
        _miniMapCanvas.DrawArc(center, WorldDistToMini(InnerBeltMin), 0, Mathf.Tau, 36, InnerBeltRingColor * 0.6f, 1.0f);
        _miniMapCanvas.DrawArc(center, WorldDistToMini(MidBeltMin), 0, Mathf.Tau, 40, MidBeltRingColor * 0.5f, 1.0f);
        _miniMapCanvas.DrawArc(center, WorldDistToMini(OuterBeltMin), 0, Mathf.Tau, 44, OuterBeltRingColor * 0.4f, 1.0f);

        var player = PlayerCore.Instance;
        Vector2 playerWorldPos = player?.GlobalPosition ?? Vector2.Zero;
        Vector2 playerMiniPos = WorldToMini(playerWorldPos);
        float visionRadiusWorld = GetVisionRadiusWorld();
        float visionRadiusMini = WorldDistToMini(visionRadiusWorld);

        // Asteroid di dalam jangkauan 2x viewport
        var asteroids = Asteroid.ActiveAsteroids;
        for (int i = 0; i < asteroids.Count; i++)
        {
            var ast = asteroids[i];
            if (ast == null || !IsInstanceValid(ast) || !ast.IsActiveInTree) continue;

            float dist = ast.CachedPosition.DistanceTo(playerWorldPos);
            if (dist > visionRadiusWorld) continue;

            Vector2 astMiniPos = WorldToMini(ast.CachedPosition);
            if (astMiniPos.DistanceTo(center) > radius - 2.0f) continue;

            Color resCol = ast.IsEnergyAsteroid ? new Color(1.0f, 0.9f, 0.4f) : GetResourceColor(ast.PrimaryResource);
            _miniMapCanvas.DrawCircle(astMiniPos, ast.IsEnergyAsteroid ? 2.5f : 1.8f, resCol);
        }

        // War Fog Shroud pada Mini-Map
        DrawWarFogCover(_miniMapCanvas, canvasSize, playerMiniPos, visionRadiusMini);

        // Lingkaran Sensor di sekitar pemain
        _miniMapCanvas.DrawArc(playerMiniPos, visionRadiusMini, 0, Mathf.Tau, 32, SensorRingColor * 0.8f, 1.2f);

        // Black Hole (Selalu Digambar Menembus Fog!)
        var rifts = AsteroidFieldManager.Instance?.CosmicRifts;
        if (rifts != null)
        {
            for (int r = 0; r < rifts.Count; r++)
            {
                var rift = rifts[r];
                if (rift == null || !IsInstanceValid(rift)) continue;

                Vector2 riftMiniPos = WorldToMini(rift.GlobalPosition);
                _miniMapCanvas.DrawCircle(riftMiniPos, 4.0f, BlackHoleColor);
                _miniMapCanvas.DrawArc(riftMiniPos, 6.0f + _pulseTimer * 3.0f, 0, Mathf.Tau, 16, new Color(0.9f, 0.3f, 1.0f, 1.0f - _pulseTimer), 1.0f);
            }
        }

        // Pemain Blip
        _miniMapCanvas.DrawCircle(playerMiniPos, 3.5f, PlayerMarkerColor);
        _miniMapCanvas.DrawArc(playerMiniPos, 6.0f + _pulseTimer * 4.0f, 0, Mathf.Tau, 16, new Color(PlayerMarkerColor.R, PlayerMarkerColor.G, PlayerMarkerColor.B, 1.0f - _pulseTimer), 1.0f);
    }

    // =========================================================================
    // METODE BANTU RENDERING (FOG OF WAR, BELTS, BLACK HOLE, PLAYER)
    // =========================================================================

    /// <summary>
    /// Menggambar selubung War Fog di luar lingkaran penglihatan 2x viewport pemain.
    /// Menggunakan 36 quad segitiga melingkar keluar hingga batas kanvas.
    /// </summary>
    private static void DrawWarFogCover(Control canvas, Vector2 canvasSize, Vector2 centerHole, float holeRadius)
    {
        const int Segments = 36;
        float step = Mathf.Tau / Segments;
        float outerReach = MathF.Max(canvasSize.X, canvasSize.Y) * 2.2f;

        // 1. Soft feather inner ring (Pudar halus dari tepi sensor ke luar)
        float innerFeather = holeRadius * 0.90f;
        Color c0 = new(WarFogColor.R, WarFogColor.G, WarFogColor.B, 0.0f);
        Color cMid = new(WarFogColor.R, WarFogColor.G, WarFogColor.B, WarFogColor.A * 0.45f);
        Color cOut = WarFogColor;

        for (int i = 0; i < Segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;

            Vector2 vInner1 = centerHole + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * innerFeather;
            Vector2 vInner2 = centerHole + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * innerFeather;
            Vector2 vMid1 = centerHole + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * holeRadius;
            Vector2 vMid2 = centerHole + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * holeRadius;

            DrawQuadPolygon(canvas, vInner1, vMid1, vMid2, vInner2, c0, cMid, cMid, c0);
        }

        // 2. Selubung pekat War Fog di luar radius sensor
        for (int i = 0; i < Segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;

            Vector2 vMid1 = centerHole + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * holeRadius;
            Vector2 vMid2 = centerHole + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * holeRadius;
            Vector2 vOut1 = centerHole + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * outerReach;
            Vector2 vOut2 = centerHole + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * outerReach;

            DrawQuadPolygon(canvas, vMid1, vOut1, vOut2, vMid2, cMid, cOut, cOut, cMid);
        }
    }

    /// <summary>
    /// Menggambar sabuk kosmik berupa pita melingkar berbayang dengan cincin batas tipis.
    /// </summary>
    private static void DrawConcentricBeltBand(Control canvas, Vector2 center, float minR, float maxR, Color bandColor, Color ringColor, string label)
    {
        const int Segments = 54;
        float step = Mathf.Tau / Segments;

        for (int i = 0; i < Segments; i++)
        {
            float a1 = i * step;
            float a2 = (i + 1) * step;

            Vector2 v1 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * minR;
            Vector2 v2 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * minR;
            Vector2 v3 = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * maxR;
            Vector2 v4 = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * maxR;

            DrawQuadPolygon(canvas, v1, v4, v3, v2, bandColor, bandColor, bandColor, bandColor);
        }

        // Cincin Batas Dalam & Luar
        canvas.DrawArc(center, minR, 0, Mathf.Tau, 64, ringColor, 1.0f);
        canvas.DrawArc(center, maxR, 0, Mathf.Tau, 64, ringColor, 1.0f);

        // Label Nama Sabuk di Bagian Atas Arc
        Vector2 textPos = center + new Vector2(0, -((minR + maxR) * 0.5f));
        canvas.DrawString(ThemeDB.FallbackFont, textPos + new Vector2(0, 3), label, HorizontalAlignment.Center, -1, 10, ringColor.Lightened(0.2f));
    }

    private static void DrawConcentricBelt(Control canvas, Vector2 center, float radius, float width, Color color, bool dashed, string label)
    {
        canvas.DrawArc(center, radius, 0, Mathf.Tau, 56, color, 1.2f);
        Vector2 textPos = center + new Vector2(0, -radius - 4.0f);
        canvas.DrawString(ThemeDB.FallbackFont, textPos, label, HorizontalAlignment.Center, -1, 10, color);
    }

    /// <summary>
    /// Menggambar marker Black Hole (Cosmic Rift) yang selalu menembus War Fog.
    /// </summary>
    private void DrawCosmicRiftMarker(Control canvas, Vector2 mapPos, CosmicRift rift, Vector2 playerWorldPos)
    {
        float distToPlayer = rift.GlobalPosition.DistanceTo(playerWorldPos);

        // 1. Singularity Core (Pusat Hitam Pekat dengan Horizon Menyala)
        canvas.DrawCircle(mapPos, 7.0f, new Color(0.01f, 0.005f, 0.02f, 1.0f));
        canvas.DrawArc(mapPos, 7.5f, 0, Mathf.Tau, 24, BlackHoleColor, 2.0f);

        // 2. Gelombang Gravitasi / Lensing Spacetime Berdenyut
        float rippleRadius = 9.0f + _pulseTimer * 16.0f;
        float rippleAlpha = 1.0f - _pulseTimer;
        canvas.DrawArc(mapPos, rippleRadius, 0, Mathf.Tau, 24, new Color(BlackHoleColor.R, BlackHoleColor.G, BlackHoleColor.B, rippleAlpha * 0.75f), 1.2f);

        // 3. Diamond Anomaly Crosshair
        float chLen = 13.0f;
        canvas.DrawLine(mapPos + new Vector2(-chLen, 0), mapPos + new Vector2(chLen, 0), BlackHoleColor * 0.85f, 1.0f);
        canvas.DrawLine(mapPos + new Vector2(0, -chLen), mapPos + new Vector2(0, chLen), BlackHoleColor * 0.85f, 1.0f);

        // 4. Label & Telemetri
        string stateStr = rift.State switch
        {
            CosmicRift.RiftState.Dormant => "DORMANT",
            CosmicRift.RiftState.Charging => "CHARGING // INFALL",
            CosmicRift.RiftState.SupernovaBlast => "SUPERNOVA BLAST",
            CosmicRift.RiftState.WhiteHole => "WHITE HOLE GENESIS",
            _ => "ANOMALY"
        };

        string bhTag = $"BH-{rift.RiftId + 1} [{stateStr}]";
        string distTag = $"{distToPlayer:0} px";

        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(16, -2), bhTag, HorizontalAlignment.Left, -1, 10, BlackHoleColor.Lightened(0.2f));
        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(16, 10), distTag, HorizontalAlignment.Left, -1, 9, new Color(0.8f, 0.65f, 0.95f, 0.8f));
    }

    /// <summary>
    /// Menggambar marker Player Core lengkap dengan arah rotasi heading & sonar ripple.
    /// </summary>
    private void DrawPlayerCoreMarker(Control canvas, Vector2 mapPos, PlayerCore? player)
    {
        // 1. Gelombang Sonar Beacon yang berdenyut meluas
        float pulseRad = 6.0f + _pulseTimer * 18.0f;
        float pulseA = 1.0f - _pulseTimer;
        canvas.DrawArc(mapPos, pulseRad, 0, Mathf.Tau, 24, new Color(PlayerMarkerColor.R, PlayerMarkerColor.G, PlayerMarkerColor.B, pulseA * 0.85f), 1.4f);

        // 2. Titik Pusat Kapal Induk
        canvas.DrawCircle(mapPos, 5.0f, PlayerMarkerColor);
        canvas.DrawCircle(mapPos, 2.5f, Colors.White);

        // 3. Panah Heading Arah Hadap Kapal
        float headingAngle = player?.Rotation ?? 0.0f;
        Vector2 forwardDir = new(MathF.Cos(headingAngle), MathF.Sin(headingAngle));
        Vector2 rightDir = new(-forwardDir.Y, forwardDir.X);

        Vector2 tip = mapPos + forwardDir * 14.0f;
        Vector2 leftWing = mapPos - forwardDir * 6.0f + rightDir * 6.0f;
        Vector2 rightWing = mapPos - forwardDir * 6.0f - rightDir * 6.0f;

        DrawQuadPolygon(canvas, tip, leftWing, mapPos, rightWing, PlayerMarkerColor, PlayerMarkerColor * 0.7f, PlayerMarkerColor, PlayerMarkerColor * 0.7f);

        // 4. Label Pemain & Koordinat
        Vector2 pPos = player?.GlobalPosition ?? Vector2.Zero;
        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(16, -2), "YOU // PLAYER CORE", HorizontalAlignment.Left, -1, 10, PlayerMarkerColor);
        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(16, 10), $"[{pPos.X:0}, {pPos.Y:0}]", HorizontalAlignment.Left, -1, 9, new Color(0.7f, 0.95f, 1.0f, 0.8f));
    }

    /// <summary>
    /// Menggambar marker ancaman Rival Hive Core musuh jika berada dalam jangkauan sensor.
    /// </summary>
    private static void DrawRivalCoreMarker(Control canvas, Vector2 mapPos, EnemyCore enemy)
    {
        canvas.DrawCircle(mapPos, 5.5f, RivalThreatColor);
        canvas.DrawArc(mapPos, 9.0f, 0, Mathf.Tau, 16, RivalThreatColor, 1.4f);

        float ch = 10.0f;
        canvas.DrawLine(mapPos + new Vector2(-ch, -ch), mapPos + new Vector2(ch, ch), RivalThreatColor, 1.2f);
        canvas.DrawLine(mapPos + new Vector2(ch, -ch), mapPos + new Vector2(-ch, ch), RivalThreatColor, 1.2f);

        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(14, -2), "▲ RIVAL HIVE CORE", HorizontalAlignment.Left, -1, 10, RivalThreatColor.Lightened(0.25f));
        canvas.DrawString(ThemeDB.FallbackFont, mapPos + new Vector2(14, 10), $"HP: {enemy.Health:0}/{enemy.MaxHealth:0}", HorizontalAlignment.Left, -1, 9, new Color(1.0f, 0.65f, 0.75f, 0.85f));
    }

    /// <summary>
    /// Menggambar ikon Bintang Aether (Radiant Star Diamond).
    /// </summary>
    private static void DrawAetherStarBlip(Control canvas, Vector2 pos, float radius)
    {
        Color gold = new(1.0f, 0.90f, 0.40f, 1.0f);
        Color glow = new(1.0f, 0.75f, 0.20f, 0.35f);

        canvas.DrawCircle(pos, radius * 1.6f, glow);

        float len = radius * 1.4f;
        Vector2 p0 = pos + new Vector2(0, -len);
        Vector2 p1 = pos + new Vector2(len * 0.35f, 0);
        Vector2 p2 = pos + new Vector2(0, len);
        Vector2 p3 = pos + new Vector2(-len * 0.35f, 0);
        DrawQuadPolygon(canvas, p0, p1, p2, p3, gold, gold, gold, gold);
        canvas.DrawCircle(pos, radius * 0.5f, Colors.White);
    }

    /// <summary>
    /// Mendapatkan warna representasi mineral untuk titik blip di peta.
    /// </summary>
    private static Color GetResourceColor(ResourceType res)
    {
        return res switch
        {
            ResourceType.Ferrite => new Color(0.95f, 0.82f, 0.25f),  // Gold / Kuning Amber
            ResourceType.Quartz => new Color(0.28f, 0.88f, 1.0f),    // Sian Elektrik
            ResourceType.Carbide => new Color(0.95f, 0.35f, 0.45f),  // Merah Bara Carbide
            ResourceType.Vortexum => new Color(0.25f, 0.95f, 0.55f), // Hijau Zamrud
            ResourceType.Aether => new Color(1.0f, 0.92f, 0.45f),    // Putih Emas Menyala
            _ => new Color(0.8f, 0.8f, 0.8f)
        };
    }
}
