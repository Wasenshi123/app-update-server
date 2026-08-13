<!--
PR Template สำหรับ App Update Server
ใช้สำหรับแก้ server / API / config การ host update — ไม่ใช่ Engineering Notebook ของ Feature
-->

## สรุป

<!-- อธิบายสั้น ๆ ว่าเปลี่ยนอะไร และทำไม -->

-

## ประเภทการเปลี่ยนแปลง

- [ ] API / endpoint
- [ ] กฎ versioning / pre-release
- [ ] Config (`AppNames`, path, storage)
- [ ] ความปลอดภัย / auth
- [ ] Bug fix
- [ ] เอกสาร / อื่น ๆ

## ผลกระทบ

- แอปที่กระทบ (`HemoBox` / `HemoCheckIn` / อื่น ๆ):
- Endpoint / path ที่เปลี่ยน:
- Breaking change หรือไม่: ใช่ / ไม่ใช่
  - ถ้าใช่ แผน migrate / แจ้งผู้ใช้:

## Checklist

- [ ] พฤติกรรม version check ยังถูกต้อง (มี version / ไม่มี version / pre-release)
- [ ] path rule `./apps/{APPNAME}` และ mapping ใน config สอดคล้องกัน
- [ ] ไม่ทำลาย compatibility กับ App Updater / client เดิมโดยไม่ตั้งใจ
- [ ] มี test หรือขั้นตอน verify แล้ว
- [ ] เอกสาร / README อัปเดตถ้าจำเป็น
- [ ] พร้อม merge และ deploy

## วิธีทดสอบ

1.
2.
3.

## ความเสี่ยง / Rollback

-

## หมายเหตุสำหรับ Reviewer

-
