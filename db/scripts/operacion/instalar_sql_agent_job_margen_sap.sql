-- HU-042.1 (#242). Registra en SQL Server Agent el job que corre db/jobs/sincronizar_margen_sap.ps1.
-- Correrlo UNA SOLA VEZ, después de copiar sincronizar_margen_sap.ps1 y jobs/.env a una carpeta del
-- propio servidor de SQL Server (ver db/README.md, sección "Cómo probarlo a mano").
--
-- AJUSTAR @rutaScript abajo si usaste una carpeta distinta a C:\Jobs\SapMargenSap\.
--
-- Qué hace: crea el job (si ya existe, lo borra y lo vuelve a crear, para poder re-correr este script
-- sin duplicar nada), un paso de PowerShell que ejecuta el script, un horario "cada 1 hora" (el
-- intervalo REAL lo decide SYNC_INTERVAL_MINUTOS dentro del propio .env, no este horario), y lo
-- asocia al servidor local.

USE msdb;
GO

DECLARE @nombreJob NVARCHAR(200) = N'Sincronizar margen SAP (Cotifly, HU-042.1)';
DECLARE @rutaScript NVARCHAR(500) = N'C:\Jobs\SapMargenSap\sincronizar_margen_sap.ps1'; -- <-- AJUSTAR AQUÍ

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = @nombreJob)
BEGIN
    EXEC msdb.dbo.sp_delete_job @job_name = @nombreJob;
    PRINT N'Job existente eliminado, se vuelve a crear limpio.';
END
GO

DECLARE @nombreJob NVARCHAR(200) = N'Sincronizar margen SAP (Cotifly, HU-042.1)';
DECLARE @rutaScript NVARCHAR(500) = N'C:\Jobs\SapMargenSap\sincronizar_margen_sap.ps1'; -- <-- AJUSTAR AQUÍ (igual que arriba)
DECLARE @comandoPs NVARCHAR(600) = N'& ''' + @rutaScript + N'''';

EXEC msdb.dbo.sp_add_job
    @job_name = @nombreJob,
    @enabled = 1,
    @description = N'HU-042.1 (#242). Sincroniza dbo_cotifly.SapMargenCliente desde SAP TM. Corre cada hora; '
                  + N'el propio script decide si ya tocaba sincronizar según SYNC_INTERVAL_MINUTOS en jobs/.env. '
                  + N'Dueña: Datos (@scalderon-bs). No lo despliega ni lo toca backend-dotnet.';

EXEC msdb.dbo.sp_add_jobstep
    @job_name = @nombreJob,
    @step_name = N'Ejecutar sincronizar_margen_sap.ps1',
    @subsystem = N'PowerShell',
    @command = @comandoPs,
    @on_success_action = 1,  -- Quit the job reporting success
    @on_fail_action = 2,     -- Quit the job reporting failure
    @retry_attempts = 0;     -- Los reintentos los decide el propio script (racha de corridas), no el job

EXEC msdb.dbo.sp_add_schedule
    @schedule_name = N'Cada hora - margen SAP',
    @freq_type = 4,             -- diario
    @freq_interval = 1,
    @freq_subday_type = 8,      -- unidad: horas
    @freq_subday_interval = 1,  -- cada 1 hora, todo el día
    @active_start_time = 0;

EXEC msdb.dbo.sp_attach_schedule
    @job_name = @nombreJob,
    @schedule_name = N'Cada hora - margen SAP';

EXEC msdb.dbo.sp_add_jobserver
    @job_name = @nombreJob,
    @server_name = N'(local)';
GO

PRINT N'Job instalado. Verificación:';
SELECT j.name, j.enabled, js.step_name, js.command, s.name AS horario
FROM msdb.dbo.sysjobs j
JOIN msdb.dbo.sysjobsteps js ON js.job_id = j.job_id
JOIN msdb.dbo.sysjobschedules sch ON sch.job_id = j.job_id
JOIN msdb.dbo.sysschedules s ON s.schedule_id = sch.schedule_id
WHERE j.name = N'Sincronizar margen SAP (Cotifly, HU-042.1)';

-- Para probarlo manualmente (dispara el job ya, sin esperar la hora en punto):
--   EXEC msdb.dbo.sp_start_job @job_name = N'Sincronizar margen SAP (Cotifly, HU-042.1)';
-- Para ver el resultado de la última corrida:
--   EXEC msdb.dbo.sp_help_jobhistory @job_name = N'Sincronizar margen SAP (Cotifly, HU-042.1)', @mode = N'FULL';
