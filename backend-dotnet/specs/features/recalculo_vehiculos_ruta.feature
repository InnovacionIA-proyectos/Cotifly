# language: es
# HU-023.3 (#158) y desviación E3 de la campaña de QA (#174) — Recalcular los camiones de UNA ruta
# (POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos) cuando cambiaron sus mercancías o su peso: se le piden de nuevo
# los camiones al cubicador (con el mismo id de la ruta) y reemplazan a los de esa ruta. Es una mutación de la etapa final: exige
# If-Match (428 sin él, 409 CONFLICTO_CONCURRENCIA con una versión vieja). Solo se puede mientras la solicitud no haya llegado a Nexo:
# después, 409 ESTADO_NO_PERMITE (se usa la re-solicitud con una versión nueva). Cada intento queda en EventosHerramienta
# (recalcular_vehiculos_ruta, sin montos; desviación E1).
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers, el Nexo falso y el cubicador falso (nunca los reales). Pasos
# en Cotifly.Aceptacion.Tests/Costeo/clsPasosRecalculoVehiculos.cs.
@integracion
Característica: Recalcular los camiones de una ruta con el cubicador
  Como comercial de Botero Soto
  Quiero volver a pedir los camiones de una ruta cuando cambió su carga
  Para que Nexo costee los vehículos correctos sin rehacer la solicitud

  Antecedentes:
    Dado que el cubicador tiene el tipo de mercancía "Café en estibas" de las mercancías del formulario
    Y la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 3S3      | FURGON     | 28500   |

  Escenario: Antes de llegar a Nexo el recálculo reemplaza los camiones de la ruta y queda auditado
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "error_envio"
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 2S2      | FURGON     | 16000   |
      | C2       | ESTACAS    | 12500   |
    Cuando "ana" recalcula los camiones de la ruta 1 de "C1" con el ETag vigente
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la ruta 1 de "C1" tiene los camiones "2S2|16000.00, C2|12500.00"
    Y el recálculo de la ruta 1 de "C1" queda en la auditoría como exitoso
    Y la cotización "C1" sigue en el estado de costeo "error_envio" sin llegar a Nexo

  Escenario: Sin If-Match el recálculo se rechaza y los camiones no cambian
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" recalcula los camiones de la ruta 1 de "C1" sin If-Match
    Entonces la respuesta tiene el estado 428
    Y la ruta 1 de "C1" tiene los camiones "3S3|28500.00"

  Escenario: Con una versión vieja el recálculo es un conflicto de concurrencia
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" recalcula los camiones de la ruta 1 de "C1" con una versión que ya no es la vigente
    Entonces la respuesta tiene el estado 409 con el código de negocio "CONFLICTO_CONCURRENCIA"
    Y la ruta 1 de "C1" tiene los camiones "3S3|28500.00"
    Y el recálculo de la ruta 1 de "C1" queda en la auditoría como rechazado por "CONFLICTO_CONCURRENCIA"

  Escenario: Con la solicitud ya en Nexo no se recalcula y se pide una versión nueva
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 2S2      | FURGON     | 16000   |
      | C2       | ESTACAS    | 12500   |
    Cuando "ana" recalcula los camiones de la ruta 1 de "C1" con el ETag vigente
    Entonces la respuesta tiene el estado 409 con el código de negocio "ESTADO_NO_PERMITE"
    Y la ruta 1 de "C1" tiene los camiones "3S3|28500.00"
    Y el recálculo de la ruta 1 de "C1" queda en la auditoría como rechazado por "ESTADO_NO_PERMITE"

  Escenario: Una ruta que no es de la cotización no se recalcula
    Dado que Nexo responde 503 al alta de solicitudes
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y "ana" recalcula los camiones de una ruta que no existe en "C1" con el ETag vigente
    Entonces la respuesta tiene el estado 400 con el código de negocio "RUTA_INEXISTENTE"
