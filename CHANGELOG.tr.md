# Sürüm notları

Performance Booster'ın her sürümü ve getirdikleri. English: [CHANGELOG.md](CHANGELOG.md).

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
