# Sürüm notları

Performance Booster'ın her sürümü ve getirdikleri. English: [CHANGELOG.md](CHANGELOG.md).

## 1.1.0 - 2026-11-02

### Yeni
- Sensörler sekmesi: bilgisayarın tüm sensörleri cihaz cihaz (işlemci, ekran kartı, anakart, bellek, diskler, ağ, pil): sıcaklıklar, fanlar, voltajlar, saat hızları, güç, yük ve daha fazlası; her biri şu anki, en düşük, en yüksek ve ortalama değeriyle. Cihazlar kapalı gelir ve yalnızca açık olanlar okunur. Grafiğini görmek için bir satıra tıkla. 0,5 ile 60 saniye arasında ya da yazdığın 1-120 tam saniyede bir okunur.
- Canlı: işlemci, ekran kartı ve disk kartları sıcaklıklarını da gösterir.
- Canlı: çalışan her uygulama simgesiyle ve işlemci, bellek, disk okuma ve yazma, ekran kartı ve ağ kullanımıyla kartların altında sürekli görünür. Adla ara, sıralamak için sütun başlığına tıkla, uygulamaları en üstte tutmak için sabitle. Kartlar ve liste 0,5 ile 60 saniye arasında (ya da yazdığın 1-120 tam saniyede) bir okunur; oyun açıkken ya da pencere başka bir pencerenin arkasındayken hiçbir şey okunmaz. O anki değeri görmek için kartın grafiğinin üzerine gel. Kartlar artık ayrıntı paneli açmaz ve modem pingi kaldırıldı.
- Sorunlar sekmesi: sorunlar Canlı sekmesinden kendi sekmesine taşındı ve nerede çözüleceklerine göre gruplandı (ayarlar, ağ, cihazlar, güncellemeler, sistem). Bellek profili, Resizable BAR ve BIOS'ta ya da donanımla değiştirilen diğer şeyler sorun değil, not olarak gösterilir.
- Yüksek sıcaklık uyarısı (Sensörler sekmesinden açana kadar kapalı): işlemci ya da ekran kartı sınırında veya üstünde kalınca bildirim; oyun açıkken ve pencere kapalıyken de. Sınırlar değiştirilebilir.

## 1.0.2 - 2026-11-02

### Yeni
- Performance Booster'ı Windows ile başlat: artık Başlangıç sekmesinin en üstünde ve tepsi menüsünde. Oturum açılınca uygulama tepside başlar; oyun modu, hassas zamanlayıcı ve güncelleme denetimleri baştan çalışır.
- Ayarların ve kartların yanındaki (i) simgesinin üzerine gelince ayrıntılı açıklama görünür: ayarın ne yaptığı ve hangi değerin ne zaman seçileceği. NVIDIA, güç, grafik, zamanlama, bellek, giriş, ağ, ağ bağdaştırıcısı ve kesme ayarlarında, ayrıca Canlı, Ağ, Oyun modu ve Gecikme sekmelerindeki kartlarda.

## 1.0.1 - 2026-11-02

### İyileştirmeler
- Oyun modu: RAM temizleyici yalnızca boş bellek gerçekten azaldığında devreye girer (varsayılan 2 GB; önceki "belleğin yarısı" sınırı buna çevrilir), en fazla 5 dakikada bir çalışır ve yalnızca Windows'un en az ihtiyaç duyduğu önbelleği boşaltır; oyunlar yüklediklerini korur.
- Oyun modu: hile koruması, katmanlar, sesli sohbet ve ses uygulamaları oyun sırasında her zaman tam hızda kalır, oyunun başlattığı her şey de öyle. Boş kalan "Tam hızda tut" listesine olağan uygulamalar geri gelir.
- Oyun açıkken güncelleme denetimleri ve cihaz güç tasarrufu denetimi oyun kapanana kadar bekler; açık pencere işlem listelerini ve ayar denetimini duraklatır.

## 1.0.0 - 2026-11-01

### Yeni
- Canlı: 60 saniyelik grafiklerle işlemci, RAM, ekran kartı, disk ve ağ; modeme ve kullanılan DNS sunucusuna ping, dalgalanma ve kayıp; düzeltmeleriyle sorunlar paneli. Bir karta tıklayınca o kaynağı en çok kullanan uygulamalar görünür, uygulama başına ağ trafiği dahil.
- İşlemler: işlemci, RAM, disk, ekran kartı ve bağlantılarıyla çalışan her uygulama; sıralama, arama, görevi sonlandırma, önceliği değiştirme, oyun listesine ekleme, oyun açıkken tam hızda tutma ya da dosya konumunu açma.
- Güncellemeler: winget ile uygulama güncellemeleri ve Windows Update ile sürücü güncellemeleri; uygulamayı sabitleme, sürümü atlama, ayrıntıları görme, bir güncellemeyi ya da hepsini iptal etme. Açık uygulamalar yalnızca senin izninle kapatılır.
- Programlar: boyutu, kurulum tarihi ve son kullanımıyla masaüstü programları ve Store uygulamaları; sessiz kaldırma, artık taraması (dosyalar Geri Dönüşüm Kutusu'na gider, kayıt defteri anahtarları önce yedeklenir) ve bozuk kayıtların kaldırılması.
- Ayarlar: güç, grafik, zamanlama, bellek, giriş, ağ ve ağ bağdaştırıcısı ayarları, NVIDIA sürücü seçenekleri, MSI modu, cihaz güç tasarrufu, kullanılmayan cihazlar ve nadiren gereken hizmetler; her biri şu anki ve önerilen değeri, etkisi ve geri alma seçeneğiyle, bir de arama kutusu.
- Oyun modu: bir oyun açıkken (listendeki ya da Windows'un tanıdığı bir oyun) oyunlar için güç planı; diğer uygulamalar düşük öncelikte, verimlilik modunda ve oyunun kullanmadığı çekirdeklerde; bellek azalınca bekleme listesi boşaltılır, Wi-Fi ağ araması duraklatılır, iki ekran kartlı bilgisayarlarda yüksek performanslı kart kullanılır. Her oyundan sonra Son oyun kartı oyunu neyin kısıtladığını gösterir. Oyunlar Defender'ın gerçek zamanlı taramasından çıkarılabilir; uygulama açıkken daha hassas Windows zamanlayıcısı tutulur.
- Gecikme: sürücü gecikmesi (sürücü ve işlemci çekirdeği başına DPC ve ISR süreleri), fare yoklama testi ve oyunlarda önemli cihazların kesmeleri.
- Ağ: yük altında gecikmeyle hız testi (bufferbloat notu) ve DNS yönlendirmesini fark eden, normal ve şifreli (DNS over HTTPS) DNS hız testi; tek tıkla daha hızlı bir DNS sunucusuna geçiş.
- Donanım ayrıntıları ve ipuçlarıyla Başlangıç ve Sistem sekmeleri, temizlik araçları ve metin rapor.
- Uygulama kendini günceller ve güncellemeden sonra neler yeni olduğunu gösterir. Türkçe ve İngilizce.
