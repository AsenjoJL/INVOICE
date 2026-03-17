Place the PostgreSQL Windows installer here so NSIS can embed it.

Expected filename (matches `Installer.nsi`):
  postgres_installer.exe

Notes:
- This repo does not commit the PostgreSQL installer (large binary).
- When building the HazelInvoice installer, NSIS will include and run this file if PostgreSQL isn't installed.
