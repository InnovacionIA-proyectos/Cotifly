# Pendientes de Sara (Datos y Frontend)

**Fecha:** 2026-10-28 · **Repo:** `Cotifly`.

## 🟠 Instalar el SQL Agent Job de HU-042.1 (#242) — bloqueado por permisos de BD

El script del job (`db/jobs/sincronizar_margen_sap.ps1`) y el que lo registra en SQL Agent
(`db/scripts/operacion/instalar_sql_agent_job_margen_sap.sql`) ya están construidos y probados —
corrido a mano varias veces desde una máquina de desarrollo, con datos reales (1276 filas,
100% con NIT resuelto). Falta solo dejarlo corriendo solo, automático, en el servidor real.

**Por qué está bloqueado, dos cosas distintas:**
1. **No tengo acceso RDP** al servidor de SQL Server (`10.10.40.85`) — hace falta para copiar ahí
   `sincronizar_margen_sap.ps1` y el `.env` real (el job solo puede ejecutar un archivo que esté
   físicamente en su propio disco, no en mi laptop).
2. **Mi usuario (`BOTEROSOTO\scalderon`) no tiene permiso para crear jobs de SQL Agent** —
   verificado: no es `sysadmin` ni pertenece a ningún rol de `msdb` (ni `SQLAgentUserRole`). Hace
   falta que alguien con permisos de administrador lo cree (o me agregue a ese rol una vez, y de
   ahí en adelante podría crearlos yo misma).

**Qué falta hacer:** pedirle a un DBA/admin que (a) copie los 2 archivos al servidor en una carpeta
(ej. `C:\Jobs\SapMargenSap\`) y (b) corra `instalar_sql_agent_job_margen_sap.sql` (ajustando la ruta
si usa otra carpeta) — o que me dé esos dos permisos para hacerlo yo.

**2026-10-28, actualización:** ya tengo a alguien que puede hacerlo (RDP + permisos). **Pendiente:
enviarle** `sincronizar_margen_sap.ps1`, `.env.example` (que él mismo llene con las claves reales ya
en el servidor — no pasarle las claves por chat) y `db/scripts/operacion/instalar_sql_agent_job_margen_sap.sql`,
más las instrucciones paso a paso (carpeta `C:\Jobs\SapMargenSap\`, probar a mano primero, luego
correr el script de instalación, verificar con `sp_start_job` + `sp_help_jobhistory`).
