# DOKUMEN ACUAN DESAIN SWARM (GEOMETRY SWARM)

Dokumen ini adalah acuan resmi arsitektur geometri, peran, siluet prosedural, dan mekanik setiap unit dalam game **Geometry Swarm**.

---

## 1. Core Structure (Pusat Komando & Pabrik)

### **The Hive Core (Hexagon Modular)**
* **Bentuk Geometri**: Hexagon beraturan dengan inti node bercahaya di tengahnya.
* **Warna Neon**: Putih Inti dengan aura Gold/Kuning Emas (`#FFF68F` / `#FFD700`).
* **Sifat & Mekanik**:
  * **Modular Attachment**: Core baru yang diproduksi/didapatkan akan menempel otomatis pada salah satu sisi hexagon core yang sudah ada (membentuk gugusan sarang lebah / *honeycomb grid*).
  * **Stat Scaling**:
    * **HP Tambahan**: Bertindak sebagai nyawa terakhir. Jika seluruh Guardian habis, musuh harus menghancurkan hexagon core satu per satu dari layer luar ke dalam.
    * **Spawn Rate Boost**: Setiap core tambahan membuka slot fabrikasi swarm baru secara simultan (+25% kecepatan produksi swarm).
    * **Cluster Mass**: Semakin besar sarang lebah, semakin stabil posisinya dari efek dorongan/knockback ledakan besar.

---

## 2. Utility & Defense Units

### **Mining Droid (Magnet U / Horseshoe)**
* **Bentuk Geometri**: Bentuk tapal kuda / magnet U dengan dua kutub di ujungnya.
* **Warna Neon**: Oranye Hangat dengan ujung kutub Cyan/Biru Listrik (`#FF8C00` & `#00FFFF`).
* **Sifat & Mekanik**:
  * **Magnetic Harvest**: Otomatis terbang ke kristal geometri terdekat saat berada di luar pertempuran.
  * **Tractor Beam**: Menembakkan sinar gaya magnetik prosedural untuk mencungkil pecahan materi (*geometry shards*), lalu membawanya kembali ke Core Hive untuk diolah menjadi swarm baru.
  * **Displacement**: Gerakan bolak-balik miner meninggalkan riakan gelombang halus yang khas pada grid arena.

### **Guardian / Shield (Kurung Tebal Ganda `(( ))`)**
* **Bentuk Geometri**: Dua pasang busur kurung tebal melengkung bersarang (`(( ))`).
* **Warna Neon**: Cyan Berlian / Shield Blue (`#00E5FF`).
* **Sifat & Mekanik**:
  * **Defensive Orbit**: Membentuk cincin perisai rapat di sekeliling gugusan Core Hexagon.
  * **Kinetic Deflection**: Memiliki ketahanan tinggi terhadap proyektil musuh. Saat peluru lawan mengenai guardian, serangan musuh akan terdefleksi atau diredam.
  * **Sacrifice Mechanism**: Jika damage terlalu besar, satu kurung akan retak dan pecah menjadi serpihan cahaya, menyisakan kurung bagian dalam sebelum unit hancur.

---

## 3. Attacker Variants (Amunisi Kawanan / Striker)

Pemain menggunakan swarm penyerang sebagai proyektil langsung. Masing-masing memiliki spesialisasi tempur:

### **A. Dart Needle (Penusuk Cepat / Standard Kinetic)**
* **Bentuk Geometri**: Segitiga sama kaki sangat lancip (seperti anak panah / jarum).
* **Warna Neon**: Kuning Terang / Electric Lime (`#CCFF00`).
* **Sifat & Mekanik**:
  * Proyektil tercepat dengan lintasan lurus (*high muzzle velocity*).
  * Mampu menembus 1–2 swarm musuh berukuran kecil sebelum kehilangan momentum.
  * Paling hemat materi untuk diproduksi dalam jumlah masif.

### **B. Giga Shuriken (Pembersih Kawanan / AoE Spinner)**
* **Bentuk Geometri**: Bintang 4 sisi tajam simetris (*quad-pointed shuriken*).
* **Warna Neon**: Magenta Elektrik / Pink Neon (`#FF1493`).
* **Sifat & Mekanik**:
  * Berotasi kencang pada porosnya saat melesat ke target.
  * Memiliki radius *hitbox* melingkar yang luas; memotong kerumunan swarm musuh secara serentak.
  * Sangat efektif untuk menangkal serbuan kawanan musuh yang bergerombol padat (*anti-swarm*).

### **C. Drill Drone (Bor Spiral / Super Giga Drill Break)**
* **Bentuk Geometri**: Mata bor bertingkat spiral (*stepped spiral drill cone*).
* **Warna Neon**: Merah Scarlet / Crimson Flare dengan percikan oranye (`#FF2400` & `#FF7700`).
* **Sifat & Mekanik**:
  * **Kinetic Spiral**: Melesat sambil berputar bor kencang, memiliki daya penetrasi tinggi menembus barisan pertahanan musuh.
  * **SUPER GIGA DRILL BREAK (Charge Mode)**:
    * Saat pemain menahan tombol **Klik Kanan**, seluruh unit Drill Drone di kawanan akan ditarik paksa menuju moncong depan kapal induk.
    * Seluruh drone bor menyatu (*merge*) dan berputar dalam pusaran vortex berenergi raksasa.
    * Semakin banyak unit Drill Drone yang dimiliki, semakin raksasa ukuran mata bor yang tercipta!
    * **Saat Dilepaskan**: Mata bor raksasa **Super Giga Drill Break** meluncur kencang dengan kecepatan ekstrem ($1.750\text{ px/s}$), memicu gelombang kejut dahsyat pada grid arena, membabat habis semua rintangan di jalurnya sebelum terpecah kembali dan terbang pulang ke Core.

### **D. Crescent Boomerang (Flanker / Otomatis Kembali)**
* **Bentuk Geometri**: Lengkungan bulan sabit tajam (*arc wing blade*).
* **Warna Neon**: Hijau Zamrud Neon (`#00FF7F`).
* **Sifat & Mekanik**:
  * Ditembakkan tidak dalam garis lurus, melainkan meliuk dalam kurva lingkaran (*flanking arc*).
  * Menghantam kawanan lawan dari sisi samping/belakang perisai mereka.
  * **Auto-Loopback**: Jika tidak hancur saat menabrak, kurva lintasannya secara natural akan melingkar kembali ke orbit Core tanpa memerlukan perintah manual.

---

## 4. Hirarki Sistem Rendisi Prosedural (Tanpa Sprite Luar)

Semua bentuk di atas digambar murni menggunakan kalkulasi vektor 2D (`PackedVector2Array`) di C# / Godot:
1. **Titik Koordinat Relatif**: Disimpan sebagai array simpul lokal berukuran normalisasi (skala 1.0).
2. **Batching GPU (`MultiMeshInstance2D`)**: Setiap tipe unit dialokasikan ke dalam satu MultiMesh untuk performa instan tanpa drop frame meski mencapai ribuan entitas.
3. **Dynamic HDR Tint**: Instancing buffer menyimpan warna dan intensitas glow masing-masing unit secara real-time.

