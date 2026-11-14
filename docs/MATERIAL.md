# DOKUMEN ACUAN MATERIAL & SISTEM EKONOMI (GEOMETRY SWARM)

Dokumen ini adalah acuan resmi taksonomi material kosmik, geologi sebaran asteroid, sistem deteksi spektrometri scanner, resep fabrikasi pabrik (*Hive Foundry*), dan siklus daur ulang puing (*shatter / salvage economy*) dalam game **Geometry Swarm**.

---

## 1. Taksonomi 5 Material Kosmik (Core Resources)

Di dalam semesta Geometry Swarm, terdapat 5 elemen dasar yang diekstraksi dari batuan kosmik untuk memproduksi armada swarm dan memperluas struktur kapal induk:

| No | Nama Mineral | Warna Neon (Hex / HDR) | Peran Utama dalam Fabrikasi | Sumber Utama |
| :---: | :--- | :--- | :--- | :--- |
| **1** | **Ferrite Alloy** | Kuning Amber<br>`#FFE033` / `Color(2.3, 2.5, 0.35)` | Rangka struktural dasar semua jenis drone; bahan baku tunggal amunisi kinetik **Dart Needle**. | Asteroid Batu Kolosal |
| **2** | **Optic Quartz** | Sian Elektrik<br>`#00E5FF` / `Color(0.3, 2.4, 3.1)` | Lensa transmisi laser & sensor optik; komponen wajib **Mining Droid**, **Scanner Drone**, **Guardian Shield**, dan **Boomerang**. | Asteroid Batu Kolosal & Geode Luar |
| **3** | **Vulcan Carbide** | Merah Bara<br>`#FF3300` / `Color(3.3, 0.65, 0.22)` | Paduan baja keras tahan panas & gesekan kinetik tinggi; bahan baku mata bor **Drill Drone**, bilah pisau **Giga Shuriken**, dan perisai **Guardian**. | Asteroid Batu Kolosal |
| **4** | **Vortexum Ore** | Hijau Zamrud<br>`#00FF66` / `Color(0.35, 2.9, 1.25)` | Kristal propelan aerodinamis; memberikan momentum rotasi pada **Giga Shuriken** dan kurva flanking pada **Crescent Boomerang**. | Asteroid Batu Kolosal |
| **5** | **Aether Crystal** | Putih-Emas Menyala<br>`#FFF68F` / `Color(3.8, 3.2, 1.15)` | Inti energi plasma murni; baterai aktif penggerak spesialisasi canggih serta bahan mitosis **Hive Core**. | **Eksklusif: Asteroid Energy (Floating Geode)** |

---

## 2. Geologi & Ekologi Sebaran Asteroid

Peta kosmik seluas $12.000 \times 12.000\text{ px}$ dibagi ke dalam 3 Sabuk Asteroid konsentris di sekeliling Markas Pusat `(0, 0)`:
* **Sabuk 1 (Inner Belt)**: Jarak $1.250\text{ - }2.200\text{ px}$ (5 Asteroid Kolosal, Radius $320\text{ - }440\text{ px}$).
* **Sabuk 2 (Mid Belt)**: Jarak $2.450\text{ - }4.250\text{ px}$ (15 Asteroid Kolosal, Radius $440\text{ - }700\text{ px}$).
* **Sabuk 3 (Outer Belt)**: Jarak $4.450\text{ - }5.500\text{ px}$ (11 Asteroid Kolosal, Radius $680\text{ - }1.050\text{ px}$).

```
[ Pusat (0,0) ] --> [ Safe Zone: R < 1100 px ] 
                 --> [ Inner Belt + 3 Cosmic Rifts: 1250 - 2200 px ]
                 --> [ Mid Belt: 2450 - 4250 px ]
                 --> [ Outer Belt: 4450 - 5500 px ]
                 --> [ Batas Sektor: 5700 px ]
```

---

### A. Asteroid Batu Kolosal (Colossal Rock Asteroid)
* **Karakteristik**: Siluet poligon batu faset gelap (`#08070B`) dengan deposit mineral tersembunyi di bawah permukaan.
* **Komposisi Mineral**: Mengandung **Ferrite, Quartz, Carbide, dan Vortexum** (tidak pernah mengandung Aether).
* **Distribusi Deposit**:
  * Memiliki $2\text{ s/d }9$ kantung deposit dengan ukuran bervariasi.
  * Peluang 65% memiliki **2 jenis mineral berbeda** di dalam satu asteroid (Primary + Secondary), dan peluang 35% memiliki deposit raksasa (*Motherlode Core*).
* **Ketahanan Struktural Voxel 13 px**:
  * Setiap blok batu biasa memiliki HP masif ($\text{MaxHP} = 45.0$) terhadap benturan kinetik drone biasa, sehingga **hanya Mining Laser** yang dapat memotong lorong asteroid dengan cepat dan efisien.

---

### B. Asteroid Khusus: Bintang Mini Singularitas Aether (*Quantum Micro-Star Singularity*)
* **Karakteristik & Skala**: Anomali energi kosmik fusi murni berbobot massa ekstrem berukuran kompak ($R \approx 85\text{--}115\text{ px}$, sekitar $2.5\times$ ukuran kapal induk) yang memancarkan pendaran cahaya Aether putih-emas dan **melengkungkan ruang angkasa di sekelilingnya (*Gravitational Lensing*)**. Menjadi **satu-satunya sumber penghasil kristal Aether** di alam semesta.
* **Arsitektur Visual Shader (*mini_star_singularity.gdshader*)**:
  1. **Gravitational Lensing & Seamless Circular Boundary Mask**:
     * Shader membelokkan grid arena retro dan bintang latar belakang secara radial ke arah pusat gravitasi (*Einstein Ring*).
     * Dilengkapi *Radial Boundary Alpha Mask* halus yang memudarkan distorsi dan alpha ke 0 sebelum mencapai tepian kanvas, menjamin **100% bebas garis kotak / potongan tepi**.
  2. **Inti Fusi Pijar & Korona Plasma (HDR Bloom > 1.0)**:
     * Inti fusi putih-emas padat (`Color(3.8, 3.2, 1.15)`) memicu pendaran *Bloom* alami dari WorldEnvironment.
     * Dikelilingi lidah api korona emas-amber dan busur fluks medan magnetik sian elektrik yang berputar dinamis secara prosedural.
* **Medan Gravitasi Maut & Bahaya Lingkungan (*Gravitational Hazard & Event Horizon*)**:
  1. **Tarikan Gravitasi (*Accretion Well*, Jangkauan $4.5 R$)**:
     * Menyeret kapal induk pemain dan asteroid lain yang lewat menuju ke pusatnya. Pemain harus terus menembakkan thruster untuk menahan diri agar tidak tergelincir ke dalam.
  2. **Titik Kematian (*Event Horizon*, Jangkauan $0.42 R$)**:
     * **Asteroid Lain**: Jika terhisap masuk, hancur terkompresi dan lenyap ditelan singularitas.
     * **Kapal Induk Pemain**: Jika tersedot sampai ke inti bintang, kapal pemain **langsung hancur lebur (*Instant Death*)**!
* **Mekanik Penambangan, Siphoning, & Supernova Overload**:
  1. **Aliran Plasma Siphoning (*Helical Plasma Siphon Stream*)**:
     * Saat Mining Laser mengenai bintang, muncul **pilinan pita plasma heliks ganda** dan partikel energi putih-emas/sian yang mengalir cepat **mundur dari bintang ke kapal penambang**.
  2. **Durasi Penambangan Terkalibrasi & Penyerapan Permukaan**:
     * Permukaan korona plasma bintang selalu menahan berkas laser secara padat (*Surface Impact Collision*, laser tidak pernah melubangi atau menembus ke belakang).
     * Butuh waktu paparan laser kontinu selama **$15\text{ detik}$** untuk memicu destabilisasi fusi hingga meledak Supernova (panas mendingin perlahan jika tembakan terputus untuk bermanuver).
  3. **Supernova Detonation & Hujan Harta Karun Aether**:
     * Saat mencapai titik kritis ($1.0$), bintang mengalami *Gravitational Collapse* dan meledak dalam **Supernova Masif**:
       * **Dorongan Kinetik Ekstrem (*Massive Knockback*)**: Melempar kapal pemain, drone swarm, dan asteroid di sekitar hingga ratusan pixel ke belakang.
       * **Robekan Grid Latar Belakang (*Grid Rupture Shockwave*)**: Memicu gelombang kejut daya $3800$ pada fisika grid arena, merobek garis-garis grid menjadi percikan api kosmik.
       * **Harta Karun Aether**: Melontarkan **$24\text{ s/d }32$ keping Kristal Aether & Quartz** menyebar ke seluruh angkasa!

* **Siklus Reinkarnasi Abadi Bintang Aether (*Aether Seed Cycle*)**:
  1. **Tahap 1: Dormant Seed ($10\text{ detik}$)**:
     * Setelah Supernova, meninggalkan satu biji energi berpendar kecil (*Aether Seed*) yang diam di titik nol ledakan selama 10 detik, membiarkan pemain leluasa mengumpulkan kristal Aether.
  2. **Tahap 2: Fast Cosmic Drift & Spacetime Fold Teleportation ($20\text{ detik}$)**:
     * **Gerak Cepat Meluncur**: Meluncur dengan kecepatan tinggi ($540\text{ px/s}$) melintasi sabuk asteroid mencari koordinat baru yang aman selama 20 detik.
     * **Teleportasi Lipat Ruang Acak (*Quantum Spacetime Fold*)**: Setiap beberapa detik, seed melipat ruang:
       * **Spacetime Implosion**: Ruang-waktu di sekitarnya terkompresi sangat kuat (impuls grid negatif $-3400$, cincin gelombang menyusut ke titik tunggal).
       * **Sub-space Vanish**: Biji lenyap seketika ke dalam lipatan sub-space dan melompat $520\text{--}980\text{ px}$ ke koordinat baru secara acak.
       * **Spacetime Expansion**: Muncul kembali di koordinat baru dengan ledakan pelebaran ruang hebat (impuls grid positif $+3800$, shockwave mengembang masif, dan layar bergetar).
       * **Lanjut Meluncur**: Biji mekar kembali dan melanjutkan penerbangan cepatnya.
  3. **Tahap 3: Spacetime Infall & Rebirth ($30\text{ detik}$)**:
     * Tiba di lokasi baru dan berhenti.
     * Menghisap energi ruang-waktu dan partikel cahaya kosmik secara agresif selama 30 detik.
     * Mengembang perlahan dari biji kecil, mekar secara spektakuler, memancarkan shockwave kelahiran kembali, dan kembali menjadi Bintang Aether penuh!

---

### C. Aturan & Animasi Kehancuran Asteroid (*Catastrophic Destruction System*)
Sebuah asteroid tidak lenyap secara instan dalam 1 frame, melainkan mengalami sekuens kehancuran multi-tahap yang dramatis:
1. **Syarat Ambang Batas Kehancuran (*Destruction Triggers*)**:
   * **Asteroid Batu Biasa**: Runtuh saat sisa materi padatnya tersisa $\le 22\%$ (kehilangan $>78\%$ massanya), atau HP mencapai 0.
   * **Asteroid Energy**: Runtuh saat **Inti Singularitas Aether habis tertambang** (`_remainingCoreVoxels <= 0`) karena hilangnya jangkar gravitasi sentral, atau sisa materi padat $\le 28\%$, atau HP mencapai 0.
2. **Tahap 1: Overload Struktural & Getaran Katastropik ($0.52\text{ detik}$)**:
   * Kolisi fisik dinonaktifkan seketika.
   * Badan asteroid bergetar hebat dengan amplitudo meningkat ($2.5 \to 14.0\text{ px}$).
   * Pola retakan fraktur laba-laba (*spiderweb fracture lines*) menyala putih-pijar berkedip cepat melintasi seluruh badan asteroid.
3. **Tahap 2: Detonasi Final & Efek Kosmik**:
   * **Gelombang Kejut Vektor (*Expanding Shockwave Ring*)**: Cincin gelombang kejut mengembang cepat ($16 \to 1.8 R$) bergradasi pudar halus. Asteroid Energy memancarkan *Dual-Ring Shockwave* (Cincin Emas Aether di dalam + Cincin Sian Quartz di luar).
   * **Serpihan Bongkahan Poligonal (*Destruction Shards*)**: 18–24 bongkahan batu/kaca kuantum terlontar radial 360° ($120\text{--}340\text{ px/s}$) sambil berotasi bebas selama $2.0\text{--}3.2\text{ detik}$.
   * **Pelepasan Harta Karun (*Loot Ejection*)**: Seluruh sisa mineral yang masih terperangkap di dalam perut batu terlontar keluar menjadi kepingan kristal berharga yang melayang bebas untuk dikoleksi pemain.
   * **Pemekaran Anak Asteroid**: Asteroid batu raksasa ($R > 350\text{ px}$) pecah menjadi 2 anak asteroid yang terlontar berlawanan arah.

### D. Dualitas Black Hole & White Hole di Sabuk Dalam (*Tri-Pillar Celestial Gates*)
* **Peran**: Mesin regenerasi asteroid kosmik abadi (*Infinite Asteroid Engine*).
* **Posisi**: 3 portal simetris di Sabuk Dalam ($R = 1.550\text{ px}$) yang berotasi orbit lambat ($\sim 8.7$ menit per putaran).
* **Dualitas Siklus Black Hole & White Hole**:
  1. **Fase Black Hole (Dormant)**:
     * Lubang hitam pekat mutlak (diameter $\sim 92\text{ px}$) yang secara total menutup/menelan grid arena dan parallax bintang di belakangnya.
     * Dikelilingi cincin **Hawking Radiation Photon Sphere** yang ultra-tipis, tajam, dan elegan (pendaran cyan kuantum dan violet) dengan 28 fluktuasi partikel kuantum virtual yang berkedip halus di bibir horizon peristiwa.
  2. **Fase Infall (Supercharged Gravitational Inversion, $3.8\text{ detik}$)**:
     * **Getaran Hisap Eskalatif**: Mengirimkan pulsa gravitasi hisap beruntun ke grid arena ($750 \to 3800$ impulse) dengan frekuensi meningkat ($0.18\text{s} \to 0.09\text{s}$), memicu getaran kamera dahsyat (*escalating camera tremor*).
     * **Akselerasi Spin Relativistik**: Kecepatan putaran fluktuasi kuantum melesat dari $1.5\times$ hingga $28.0\times$.
     * **Retakan Ruang-Waktu Event Horizon**: Piringan hitam mengalami retakan fraktur petir berpijar putih-panas (`IncandescentCrackColor`) yang menjalar dan membakar piringan hitam dari dalam.
     * **Lengan Spiral Akresi Kuantum**: 14 lengan spiral logaritmik melengkung terhisap masuk ke dalam lubang hitam.
     * **Supercritical Pre-Eruption Pinch ($p \in [0.86, 0.98]$)**: Tepat sebelum meledak, horizon peristiwa memampat ekstrem dari $46\text{ px} \to 14\text{ px}$ memusatkan energi singularitas ke satu titik padat mutlak.
  3. **Fase White Hole & Kelahiran Asteroid ala Apple Genie Effect ($1.35\text{ detik}$)**:
     * Bertransformasi menjadi **White Hole** supernova-scale (blinding white-silver).
     * Memicu shockwave radial ganda dahsyat daya $4600$ ke grid arena dan 20 berkas laser relativistik memancar 360°.
     * **Animasi Kelahiran Apple Genie Effect ($1.65\text{ detik}$)**:
       * **Tali Pusar Plasma Terompet (*Genie Umbilical Funnel*)**: Corong plasma aetherik mengalirkan massa dari mulut White Hole ke pantat asteroid yang sedang melesat keluar, dilengkapi inti putih-perak ultra-panas dan filamen medan magnetik.
       * **Non-Uniform Taffy Stretch**: Badan asteroid ditarik membujur memanjang ($1.55\times$) sambil diperas sangat tipis di leher lubang ($0.06\times$), lalu menggelembung mekar seperti balon ($1.24\times$).
       * **Pelepasan & Getaran Elastis (*Cord Snap & Settle*)**: Pada $p = 0.75$, tali pusar putus dengan percikan plasma, memicu getaran harmonik elastis (*damped harmonic wobble*) sebelum mendingin dan memadat menjadi batuan kokoh.
  4. **Fase Cooling ($2.6\text{ detik}$)**:
     * White Hole mendingin kembali dan meredup menjadi Black Hole yang sunyi dan dingin.

---

## 3. Sistem Spektrometri Scanner (Scanner Drone Progression)

Mineral di dalam asteroid batu terbungkus lapisan kerak tebal. Kapal induk mengandalkan armada **Scanner Drone** yang terbang pasif untuk memindai kepadatan mineral dari kejauhan.

### Aturan Fisik Sensor Scanner:
1. **Kulit Luar Asteroid Tidak Menyala**: Garis tepi terluar (*outline*) asteroid batu **100% permanen berwarna batu gelap angkasa** (`_unscannedBorderColor`). Scanner hanya mendeteksi dan menembus mineral di dalam perut batu, bukan mengubah kulit batu luar menjadi lampu neon.
2. **True X-Ray Hanya Terbuka di 60+ Scanner**: Pada 5–10 scanner awal, mineral hanya tampak sebagai pendaran bayangan samar di dalam kegelapan batuan. Full X-Ray Spectrometry (kontur urat neon menyala tajam dan transparansi penuh) membutuhkan investasi armada pengintai masif ($\ge 60$ Scanner Drone).

Tingkat kejelasan visual (*scanRatio*) dihitung dengan formula kurva daya terkalibrasi:

$$\text{scanRatio} = 0.05 + 0.95 \times \left(\frac{\text{ScannerCount}}{65}\right)^{1.35} \quad (\text{dibatasi } 0.05 \text{ s/d } 1.0)$$

```
Visibilitas
  100% |                                              . - - [ Tier 4: True X-Ray Spectrometry ]
   80% |                                      . - '
   50% |                              . - ' [ Tier 2: Deep Reconnaissance ]
   25% |                    . - ' [ Tier 1: Faint Traces ]
   10% |          . - ' [ Tier 0: Ghost Shadow ]
    0% +----------------------------------------------------
       0    5    10        25        40        60        65+   Scanner Count
```

### Tabel Tingkatan Spektrometri:
| Tier | Jumlah Scanner | Nilai `scanRatio` | Efek Visual pada Asteroid |
| :---: | :---: | :---: | :--- |
| **Tier 0** | 0 Scanner | $0.05$ (5%) | **Buta Instrumen**. Seluruh deposit mineral tertutup kegelapan pekat batuan kosmik. |
| **Tier 1** | **5–10 Scanner** *(Awal Game)* | **$0.08 - 0.126$** (8%–12.6%) | **Bayangan Redup (Bukan X-Ray)**. Hanya pendaran bayangan sangat samar dan redup di dalam batu. Batas urat mineral sama sekali belum tampak. Kulit luar asteroid tetap batu gelap. |
| **Tier 2** | **20–35 Scanner** | **$0.25 - 0.46$** (25%–46%) | **Pendaran Warna Jelas**. Warna mineral (Amber, Sian, Merah, Hijau) mulai terbedakan dengan jelas di bawah lapisan kerak batuan. |
| **Tier 3** | **45–55 Scanner** | **$0.63 - 0.81$** (63%–81%) | **Urat Terbentuk**. Garis urat neon mineral (*vein border*) mulai muncul dan menyala tipis. |
| **Tier 4** | **60–65+ Scanner** | **$0.90 - 1.00$** (90%–100%) | **True X-Ray Spectrometry**. Tembus pandang sempurna! Urat mineral berpendar tajam dengan kontur neon penuh dan kejernihan spektrometri 100%. |

> [!NOTE]
> Asteroid Energy (Crystal Spire Cluster) memancarkan energi Aether terbuka ke ruang hampa, sehingga pilar-pilar kristal dan singularitas intinya selalu tampak dengan kemilau murni terlepas dari jumlah scanner.

---

## 4. Ekonomi & Resep Fabrikasi Pabrik (Hive Foundry)

Pemain membuka GUI Pabrik dengan menekan tombol **[TAB]**. Di sini, mineral yang telah dikumpulkan diolah menjadi unit swarm tempur, unit utilitas, atau memperbesar struktur Hexagon Kapal Induk.

Pabrik mendukung **Auto-Craft** (otomatis memproduksi unit setiap 0.75 detik jika bahan mencukupi):

| No | Unit / Upgrade | Output | Biaya Ferrite | Biaya Quartz | Biaya Carbide | Biaya Vortexum | Biaya Aether | Deskripsi & Fungsi Taktis |
| :---: | :--- | :---: | :---: | :---: | :---: | :---: | :---: | :--- |
| **1** | **Dart Needle** | +10 | **10** | 0 | 0 | 0 | 0 | Proyektil penusuk cepat lurus; juga berfungsi sebagai bahan bakar exhaust **Sacrificial Plasma Boost [SPACE]**. |
| **2** | **Mining Droid** | +2 | **10** | **8** | 0 | 0 | **2** | Unit penggali batu otomatis; memperlebar berkas fokus Mining Laser kapal induk. |
| **3** | **Scanner Drone** | +2 | **6** | **12** | 0 | 0 | **2** | Memperkuat daya tembus sensor spektrometri mineral asteroid di seluruh arena. |
| **4** | **Guardian Shield** | +5 | **12** | **12** | **8** | 0 | 0 | Perisai busur melengkung; otomatis membentuk formasi pertahanan 12 sektor ancaman di sekitar Core. |
| **5** | **Drill Drone** | +5 | **12** | 0 | **18** | 0 | **4** | Drone bor penetrasi tinggi; menyatu menjadi tombak raksasa saat **Super Giga Drill [RMB]** di-charge. |
| **6** | **Giga Shuriken** | +5 | **10** | 0 | **14** | **10** | 0 | Cakram bintang berotasi cepat; memotong rintangan dan gerombolan swarm dalam radius melingkar luas. |
| **7** | **Boomerang** | +5 | 0 | **8** | 0 | **16** | **4** | Senjata flanking kurva melingkar; otomatis terbang meliuk kembali ke orbit Core setelah dilepaskan. |
| **8** | **Expand Hive Core** | +1 Hex | **35** | **25** | **25** | 0 | **20** | Membelah sel kapal induk secara mitosis (+1 Hexagon Core); menambah slot nyawa dan kecepatan fabrikasi. |

---

## 5. Ekonomi Shatter & Daur Ulang Puing Swarm (Salvage Economy)

Di Geometry Swarm, kehancuran armada swarm Anda di medan tempur **tidak pernah sia-sia**. Game menerapkan hukum konservasi materi melalui sistem **Salvageable Scrap**:

```
[ Drone Tabrakan / Hancur ] 
           │
           ├───> [ Clash Sparks ] (Efek visual percikan kinetik cyan-magenta)
           ├───> [ SwarmClashImpulse ] (Riakan gelombang mikro pada grid arena)
           └───> [ Salvageable Scrap Particle ] (Kepingan rongsokan melayang di ruang hampa)
                       │
                       │ (Kapal mendekat < 180 px)
                       ▼
                 [ Tractor Beam Magnetik ] (Puing tersedot ke lambung kapal)
                       │
                       ▼
                 [ +2 Ferrite Alloy ] (Kembali ke stok gudang Pabrik)
```

### Mekanik Daur Ulang Puing:
1. **Pemicu Kehancuran (*Shatter Trigger*)**:
   * Setiap kali drone tempur (Dart, Shuriken, Boomerang, Drill) menabrak batuan asteroid atau mengalami impak benturan keras, unit tersebut meledak menjadi serpihan cahaya.
2. **Kepingan Rongsokan (*Salvageable Scrap*)**:
   * Menghasilkan partikel puing materi berukuran $4.2\text{ px}$ dengan masa bertahan melayang di ruang hampa selama **24.0 detik**.
   * Puing hanyut perlahan mengikuti inersia benturan.
3. **Penyedotan Traktor Magnetik**:
   * Kapal induk pemain memiliki medan magnet penarik puing pasif dengan radius efektif **$180.0\text{ px}$**.
   * Ketika puing berada dalam radius ini, puing akan berakselerasi tertarik lurus menuju pusat kapal dengan kecepatan hingga $780\text{ px/s}$.
4. **Konversi Material**:
   * Saat puing menyentuh badan kapal ($d \le 24\text{ px}$), puing diserap dan langsung mengembalikan **$+2\text{ Ferrite Alloy}$** ke gudang material pabrik.
   * Mekanik ini memungkinkan pemain yang aktif bermanuver di dekat pertempuran/area pengeboran untuk terus mendaur ulang amunisi swarm yang hilang tanpa kehabisan modal.

---

### Pengecualian Khusus: Pengorbanan Propelan (*Sacrificial Plasma Boost*)
* Saat pemain menahan tombol **[SPACE]**, 10 unit Dart Needle di buritan kapal dibakar habis satu per satu di dalam ruang bakar nozzle (*Focal Plasma Chamber*) untuk menghasilkan daya dorong ekstrem ($1.450\text{ px/s}$).
* Dart yang dikorbankan untuk boost **tidak menghasilkan puing rongsokan**, melainkan dikonversi 100% menjadi energi kinetik propelan plasma murni dan gelombang kejut grid arena.

