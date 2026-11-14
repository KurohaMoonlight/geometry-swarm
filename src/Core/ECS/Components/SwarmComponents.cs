using Godot;

namespace GeometrySwarm.Core.ECS.Components;

public enum SwarmState : byte
{
	Orbiting = 0,
	Launched = 1,
	Returning = 2,
	Mining = 3,
	ChargingDrill = 4, // Sedang ditarik ke pusaran pembentukan Super Giga Drill
	DrillExhaust = 5,  // Drone bagian belakang Giga Drill / Boost yang terbakar menjadi exhaust roket lalu mati
	Dead = 6,          // Unit yang telah mati/dikorbankan (dapat digunakan kembali oleh pool)
	BoostThruster = 7, // Dart Needle yang berkumpul di belakang Core (busur 3/4 lingkaran) sebagai pendorong plasma
	BoostBrake = 8     // Dart Needle yang berkumpul di depan arah gerak Core untuk pengereman retro-rocket
}

public enum SwarmRole : byte
{
	DartNeedle = 0,
	GigaShuriken = 1,
	DrillDrone = 2,
	CrescentBoomerang = 3,
	MiningDroid = 4,
	Guardian = 5,
	ScannerDrone = 6
}

public struct PositionComponent
{
	public Vector2 Value;

	public PositionComponent(Vector2 value) => Value = value;
	public PositionComponent(float x, float y) => Value = new Vector2(x, y);
}

public struct VelocityComponent
{
	public Vector2 Value;

	public VelocityComponent(Vector2 value) => Value = value;
	public VelocityComponent(float x, float y) => Value = new Vector2(x, y);
}

public struct SwarmUnitComponent
{
	public SwarmRole Role;
	public SwarmState State;
	public int OwnerId;                 // 0 = Player, 1+ = Enemy Hives / Proyektil Musuh
	public float OrbitDistance;         // Jarak orbit ideal dari Core (dengan variasi organik per unit)
	public float OrbitAngle;            // Fase osilasi / sudut orbit organik unit
	public float OrbitSpeed;            // Kecepatan angular putaran / osilasi
	public float Energy;                // HP / Durability
	public int Health;                  // Jumlah tubrukan yang dapat ditahan sebelum hancur
	public int MaxHealth;               // Kapasitas HP maksimal
	public float HitFlashTimer;         // Durasi kedip putih saat menerima damage
	public float LifeTime;              // Timer durasi terbang / terbakar
	public float MaxLifeTime;           // Batas waktu sebelum kembali atau mati (exhaust)
	public Vector2 TargetDirection;     // Vektor arah tembakan peluru / semburan exhaust
	public Vector2 TargetPosition;      // Titik tujuan utama tembakan (misal posisi kursor / Core sasaran)
	public int SpatialEntityId;         // Indeks cepat untuk sinkronisasi Spatial Hash Grid
	public float SpinAngle;             // Rotasi visual mandiri
	public float SpinRate;              // Kecepatan rotasi (misal putaran Shuriken/Bor)
	public float ShieldTargetAngle;     // Sudut orbit target yang diberikan Core ke Defender
	public int AssignedSector;          // Sektor pertahanan (0..11) yang dialokasikan Core
	public float PersonalAngleOffset;   // Offset sudut acak agar barisan tameng organik & tidak kaku
	public float PersonalRadialOffset;  // Offset radius acak di sekitar ShieldRadius (120px)
	public int FormationIndex;          // Indeks urutan formasi pada Super Giga Drill / Boost Nozzle
	public float ExhaustTriggerTime;    // Waktu (detik) saat drone bagian belakang meledak jadi exhaust roket

	public static int GetMaxHealthForRole(SwarmRole role, SwarmState state)
	{
		if (state == SwarmState.Launched) return 1; // Drone yang diluncurkan langsung 1 hit hancur

		return role switch
		{
			SwarmRole.Guardian => 5,          // Defender: 5 kali tubrukan baru hancur
			SwarmRole.MiningDroid => 1,       // Utility rapuh: 1 HP
			SwarmRole.ScannerDrone => 1,      // Utility rapuh: 1 HP
			SwarmRole.DartNeedle => 2,        // Attacker di mode swarm/orbit: 2 HP
			SwarmRole.DrillDrone => 2,        // Attacker di mode swarm/orbit: 2 HP
			SwarmRole.GigaShuriken => 2,      // Attacker di mode swarm/orbit: 2 HP
			SwarmRole.CrescentBoomerang => 2, // Attacker di mode swarm/orbit: 2 HP
			_ => 1
		};
	}

	public SwarmUnitComponent(SwarmRole role, int ownerId, float orbitDist, float orbitSpd)
	{
		Role = role;
		State = SwarmState.Orbiting;
		OwnerId = ownerId;
		OrbitDistance = orbitDist;
		OrbitAngle = GD.Randf() * Mathf.Tau;
		OrbitSpeed = orbitSpd;
		Energy = 100.0f;
		MaxHealth = GetMaxHealthForRole(role, State);
		Health = MaxHealth;
		HitFlashTimer = 0.0f;
		LifeTime = 0.0f;
		MaxLifeTime = 1.3f;
		TargetDirection = Vector2.Right;
		TargetPosition = Vector2.Zero;
		SpatialEntityId = -1;
		SpinAngle = GD.Randf() * Mathf.Tau;

		int initialSector = (int)(GD.Randf() * 12.0f) % 12;
		AssignedSector = initialSector;
		PersonalAngleOffset = (GD.Randf() - 0.5f) * (Mathf.Tau / 12.0f) * 0.85f;
		PersonalRadialOffset = (GD.Randf() - 0.5f) * 22.0f;
		ShieldTargetAngle = (initialSector + 0.5f) * (Mathf.Tau / 12.0f) - Mathf.Pi + PersonalAngleOffset;
		FormationIndex = 0;
		ExhaustTriggerTime = -1.0f;

		// Spin rate bervariasi sesuai tipe peran
		SpinRate = role switch
		{
			SwarmRole.GigaShuriken => 24.0f,
			SwarmRole.DrillDrone => 32.0f,
			SwarmRole.CrescentBoomerang => 12.0f,
			_ => 0.0f
		};
	}
}

public struct VisualComponent
{
	public Color Color;
	public float Scale;
	public Color BaseColor; // Warna default berdasarkan peran

	public VisualComponent(Color color, float scale = 1.0f)
	{
		Color = color;
		BaseColor = color;
		Scale = scale;
	}
}
