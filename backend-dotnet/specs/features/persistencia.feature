# language: es
# HU-003.3 (#9) — Persistencia en SQL Server 2019 (ADR-008, ADR-010).
# Se ejecuta en Cotifly.Aceptacion.Tests contra SQL Server 2019 en Testcontainers,
# con el esquema de db/migrations_mssql/0001 a 0010 (HU-021, ADR-019: CotizacionesSolicitud y su agregado) y los repositorios EF Core reales.
# Folios escritos a mano con la forma heredada del Python (PCN-DDMMAAAA[-vN]): el repositorio guarda el folio que
# recibe; el formato del servidor (ADR-011, PCN-[nit-]INICIALES-DDMMAAAA-NNN[-vN]) se prueba en folio.feature. Cada escenario usa un folio distinto
# porque la base es compartida durante la corrida.
@integracion
Característica: Persistencia de cotizaciones en SQL Server 2019
  Como backend de Cotifly
  Quiero guardar y consultar las cotizaciones en dbo_cotifly
  Para que no se pierdan y cada comercial vea y modifique solo las suyas

  Antecedentes:
    Dado el esquema dbo_cotifly creado con los scripts 0001 a 0010
    Y el comercial "ana.perez" registrado
    Y el comercial "luis.gomez" registrado

  Escenario: Una cotización se guarda y se recupera por su folio
    Cuando "ana.perez" guarda la cotización "PCN-24092026" de la línea "Nacional" por 1500000 COP
    Entonces al consultar el folio "PCN-24092026" se obtiene la línea "Nacional" y el precio 1500000
    Y la cotización "PCN-24092026" está en estado de envío "sin_enviar" y versión 1

  Escenario: El folio es único
    Dado que "ana.perez" guardó la cotización "PCN-24092026-v2"
    Cuando "luis.gomez" intenta guardar otra cotización con el folio "PCN-24092026-v2"
    Entonces la operación se rechaza con el código "FOLIO_DUPLICADO"

  Escenario: Un comercial no ve ni modifica las cotizaciones de otro
    Dado que "ana.perez" guardó la cotización "PCN-25092026"
    Cuando "luis.gomez" consulta sus cotizaciones
    Entonces no ve el folio "PCN-25092026"
    Y "luis.gomez" no puede marcar como ganada la cotización "PCN-25092026"
    Pero "ana.perez" sí puede marcar como ganada la cotización "PCN-25092026"

  Escenario: updatedAt se actualiza al modificar la cotización
    Dado que "ana.perez" guardó la cotización "PCN-26092026-v3" el "2026-09-24T08:00:00-05:00"
    Cuando "ana.perez" marca como ganada la cotización "PCN-26092026-v3" el "2026-09-24T10:30:00-05:00"
    Entonces el updatedAt de la cotización "PCN-26092026-v3" es "2026-09-24T10:30:00-05:00"
    Y el createdAt de la cotización "PCN-26092026-v3" sigue siendo "2026-09-24T08:00:00-05:00"
    Y la cotización "PCN-26092026-v3" está en estado de envío "sin_enviar" y versión 3
