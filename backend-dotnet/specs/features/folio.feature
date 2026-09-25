# language: es
# HU-003.6 (#12, parte B) — Folio generado por el servidor (ADR-011, aprobado el 2026-09-25):
#   PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]
# NNN es el consecutivo diario del comercial, la fecha es la de Bogotá y -vN solo aparece en versiones nuevas.
# Se ejecuta en Cotifly.Aceptacion.Tests con IClsGeneradorFolioService real y SQL Server 2019 (sp_getapplock).
# Cada escenario usa un día distinto para no compartir consecutivos con otros escenarios.
@integracion
Característica: Folio de la cotización generado por el servidor
  Como comercial de Botero Soto
  Quiero que cada cotización reciba un folio único, legible y con la fecha del día
  Para identificarla ante el cliente sin depender de lo que escriba el asistente

  Antecedentes:
    Dado el comercial de folios "folio.ana"
    Y el comercial de folios "folio.luis"

  Escenario: La primera cotización del día sin NIT recibe el consecutivo 001
    Dado que en Bogotá son las "2031-01-05 09:30"
    Cuando "folio.ana" crea una cotización sin NIT
    Entonces el folio creado es "PCN-05012031-001" con versión 1

  Escenario: El consecutivo es diario por comercial y el NIT se guarda solo con sus dígitos
    Dado que en Bogotá son las "2031-01-06 10:00"
    Cuando "folio.ana" crea una cotización con el NIT "900.123.456-7"
    Y "folio.ana" crea una cotización con el NIT "800111222"
    Y "folio.luis" crea una cotización con el NIT "860000111"
    Entonces los folios creados son:
      | comercial  | folio                        |
      | folio.ana  | PCN-9001234567-06012031-001  |
      | folio.ana  | PCN-800111222-06012031-002   |
      | folio.luis | PCN-860000111-06012031-001   |

  Escenario: Un folio sin NIT que ya tomó otro comercial se salta
    Dado que en Bogotá son las "2031-01-07 08:00"
    Cuando "folio.ana" crea una cotización sin NIT
    Y "folio.luis" crea una cotización sin NIT
    Entonces los folios creados son:
      | comercial  | folio            |
      | folio.ana  | PCN-07012031-001 |
      | folio.luis | PCN-07012031-002 |

  Escenario: La fecha del folio es la de Bogotá aunque en UTC ya sea el día siguiente
    Dado que el reloj UTC marca "2031-01-09 02:30"
    Cuando "folio.ana" crea una cotización sin NIT
    Entonces el folio creado es "PCN-08012031-001" con versión 1

  Escenario: Una versión nueva reutiliza el folio base de la v1 con el sufijo -vN
    Dado que en Bogotá son las "2031-01-10 11:00"
    Y "folio.ana" crea una cotización con el NIT "900123456"
    Cuando "folio.ana" crea una versión nueva de su última cotización
    Y "folio.ana" crea una versión nueva de su última cotización
    Entonces los folios de la familia son "PCN-900123456-10012031-001", "PCN-900123456-10012031-001-v2" y "PCN-900123456-10012031-001-v3"
    Y la última cotización de "folio.ana" tiene la versión 3

  Escenario: Diez cotizaciones simultáneas del mismo comercial reciben folios distintos
    Dado que en Bogotá son las "2031-01-11 15:00"
    Cuando "folio.ana" crea 10 cotizaciones sin NIT al mismo tiempo
    Entonces se crean 10 folios distintos del "PCN-11012031-001" al "PCN-11012031-010"

  Escenario: Después del consecutivo 999 no se generan más folios ese día
    Dado que en Bogotá son las "2031-01-12 16:00"
    Y que "folio.ana" ya tiene la cotización "PCN-12012031-999"
    Cuando "folio.ana" crea una cotización sin NIT
    Entonces la creación se rechaza con el código "FOLIO_CONSECUTIVO_AGOTADO"

  Escenario: No se puede versionar la cotización de otro comercial
    Dado que en Bogotá son las "2031-01-13 09:00"
    Y "folio.luis" crea una cotización sin NIT
    Cuando "folio.ana" crea una versión nueva de la última cotización de "folio.luis"
    Entonces la creación se rechaza con el código "COTIZACION_NO_ENCONTRADA"
