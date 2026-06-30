-- Relation type kardinalite tanımları
INSERT INTO files.relation_type_config (relation_type, cardinality, description) VALUES
  ('cv',                'single', 'Özgeçmiş — her an yalnız bir aktif'),
  ('photo',             'single', 'Fotoğraf — her an yalnız bir aktif'),
  ('official_document', 'single', 'Resmi evrak — her an yalnız bir aktif'),
  ('document',          'multi',  'Genel belge — birden fazla aktif olabilir'),
  ('attachment',        'multi',  'Ek dosya — birden fazla aktif olabilir'),
  ('report',            'multi',  'Rapor — birden fazla aktif olabilir')
ON CONFLICT (relation_type) DO NOTHING;

-- Uygulama policy'leri
INSERT INTO files.app_policies
  (app_code, allowed_domains, allowed_file_types, can_create, can_read, can_archive, max_file_size_bytes)
VALUES
  ('yonetimapi', ARRAY['personnel'], ARRAY['photo','cv','official_document','document','attachment'],        true, true, true, 10485760),
  ('filoapi',    ARRAY['fleet'],     ARRAY['photo','document','official_document','attachment','report'],    true, true, true, 20971520)
ON CONFLICT (app_code) DO NOTHING;

-- Storage probe dosyası için kayıt gerekmez; fiziksel dosya volume'dan gelir.

-- Personel dizini (UI arama için)
INSERT INTO yonetim.personnel (personnel_id, display_name, department, title) VALUES
  ('HR001',  'Zeynep Kaya',   'HR',         'HR'),
  ('ADM001', 'Admin User',    'IT',         'Admin'),
  ('M001',   'Ayse Demir',    'IT',         'Manager'),
  ('M002',   'Mehmet Arslan', 'Finance',    'Manager'),
  ('M003',   'Elif Sahin',    'Operations', 'Manager'),
  ('P001',   'Ahmet Yilmaz',  'IT',         'Personel'),
  ('P002',   'Can Aydin',     'IT',         'Personel'),
  ('P003',   'Deniz Koc',     'IT',         'Personel'),
  ('P004',   'Ece Polat',     'IT',         'Personel'),
  ('P005',   'Burak Celik',   'IT',         'Personel'),
  ('P006',   'Selin Kurt',    'IT',         'Personel'),
  ('P007',   'Onur Aslan',    'IT',         'Personel'),
  ('P008',   'Melis Ozturk',  'Finance',    'Personel'),
  ('P009',   'Emre Yildiz',   'Finance',    'Personel'),
  ('P010',   'Naz Kaplan',    'Finance',    'Personel'),
  ('P011',   'Kaan Ergin',    'Finance',    'Personel'),
  ('P012',   'Aylin Bozkurt', 'Finance',    'Personel'),
  ('P013',   'Umut Aksoy',    'Finance',    'Personel'),
  ('P014',   'Buse Sen',      'Finance',    'Personel'),
  ('P015',   'Mert Kara',     'Operations', 'Personel'),
  ('P016',   'Derya Gunes',   'Operations', 'Personel'),
  ('P017',   'Tuna Eren',     'Operations', 'Personel'),
  ('P018',   'Esra Bilir',    'Operations', 'Personel'),
  ('P019',   'Kerem Tas',     'Operations', 'Personel'),
  ('P020',   'Nil Ozdemir',   'Operations', 'Personel'),
  ('P021',   'Alp Keskin',    'Operations', 'Personel'),
  ('P022',   'Seda Acar',     'Sales',      'Personel'),
  ('P023',   'Okan Durmaz',   'Sales',      'Personel'),
  ('P024',   'Irem Uslu',     'Sales',      'Personel')
ON CONFLICT (personnel_id) DO UPDATE SET
  display_name = EXCLUDED.display_name,
  department = EXCLUDED.department,
  title = EXCLUDED.title;

-- Yönetici-personel ilişkisi (test verisi)
INSERT INTO yonetim.team_members (manager_id, personnel_id) VALUES
  ('M001', 'P001'), ('M001', 'P002'), ('M001', 'P003'), ('M001', 'P004'), ('M001', 'P005'), ('M001', 'P006'), ('M001', 'P007'),
  ('M002', 'P008'), ('M002', 'P009'), ('M002', 'P010'), ('M002', 'P011'), ('M002', 'P012'), ('M002', 'P013'), ('M002', 'P014'),
  ('M003', 'P015'), ('M003', 'P016'), ('M003', 'P017'), ('M003', 'P018'), ('M003', 'P019'), ('M003', 'P020'), ('M003', 'P021')
ON CONFLICT DO NOTHING;
