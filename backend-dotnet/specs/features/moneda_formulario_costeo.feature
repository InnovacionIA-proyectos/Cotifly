# language: es
# HU-013.7 (#103, ADR-018, ADR-014 §5) — Cotizaciones en USD en el formulario de costeo: `moneda` en
# POST /web/solicitudes-costeo, con el mismo contrato que POST /web/licitaciones. La TRM se fija al crear con
# clsFijacionMonedaService; sin TRM no se guarda ni se envía nada a Nexo. El costo sigue en COP; el precio del flete y de los
# conexos se convierte con la TRM fijada y el PDF lleva la nota de TRM mínima. Con fuentes de TRM falsas (DolarApi principal,
# Superfinanciera de respaldo), un Nexo falso, la API real en memoria y SQL Server 2019 en Testcontainers. Margen de la línea: 15 %.
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosMonedaCosteo.cs (#103). La tarifa en USD se imprime como en HU-012.3
# (prefijo "US$" pegado al número, 2 decimales con coma).
@integracion @trm
Característica: Moneda en el formulario de costeo
  Como comercial de Botero Soto
  Quiero cotizar en dólares desde el formulario de costeo
  Para atender a los clientes que negocian en USD con la TRM del día

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y la TRM del día es 4000 según la fuente principal

  Escenario: Sin moneda la cotización es en COP
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" está en "COP" con la TRM aplicada 0

  Escenario: En USD la TRM se fija al crear y el precio se convierte
    Cuando "ana" envía a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "U1" está en "USD" con la TRM aplicada 4000
    Cuando Nexo costea la solicitud de "U1" con un flete de 2550000
    Y el polling consulta a Nexo
    Entonces el costeo de "U1" tiene el precio final 750.00 en "USD" con la TRM aplicada 4000
    Y el costo de "U1" guardado es 2550000 en COP

  Escenario: Los servicios conexos también se convierten con la TRM fijada
    Dado que "ana" envió a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Y Nexo costeó la solicitud de "U1" con un flete de 2550000 y el conexo "Cargue" de 85000 y el polling lo registró
    Entonces el servicio conexo "Cargue" de "U1" tiene el precio 25.00

  Escenario: La TRM no cambia aunque el costeo llegue otro día
    Dado que "ana" envió a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Y la TRM del día cambia a 4200
    Cuando Nexo costea la solicitud de "U1" con un flete de 2550000
    Y el polling consulta a Nexo
    Entonces el costeo de "U1" tiene el precio final 750.00 en "USD" con la TRM aplicada 4000

  Escenario: Si la fuente principal falla se usa la de respaldo
    Dado que la fuente principal de la TRM no responde y la de respaldo da 4010
    Cuando "ana" envía a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "U1" está en "USD" con la TRM aplicada 4010

  Escenario: Sin TRM no se guarda nada ni se envía a Nexo
    Dado que ninguna fuente de la TRM responde
    Cuando "ana" envía a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Entonces la respuesta tiene el estado 400 con el código de negocio "TRM_NO_DISPONIBLE"
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"
    Y Nexo no recibió solicitudes

  Escenario: Sin TRM en COP no importa
    Dado que ninguna fuente de la TRM responde
    Cuando "ana" envía a costeo en "COP" el formulario ampliado del cliente "900555111" como "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato

  Esquema del escenario: La moneda se acepta sin distinguir mayúsculas ni espacios
    Cuando "ana" envía a costeo en "<moneda>" el formulario ampliado del cliente "900555111" como "U1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "U1" está en "USD" con la TRM aplicada 4000

    Ejemplos:
      | moneda |
      | usd    |
      |  Usd   |

  Esquema del escenario: Otra moneda se rechaza sin guardar nada
    Cuando "ana" envía a costeo en "<moneda>" el formulario ampliado del cliente "900555111" como "X1"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

    Ejemplos:
      | moneda |
      | EUR    |
      | dólar  |

  Escenario: El PDF en USD lleva la nota de TRM mínima
    Dado que "ana" envió a costeo en "USD" el formulario ampliado del cliente "900555111" como "U1"
    Y Nexo costeó la solicitud de "U1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" genera el PDF del costeo de "U1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato
    Y el PDF de "U1" muestra la tarifa "US$750,00"
    Y el PDF de "U1" contiene "Esta cotización está bajo una TRM mínima de $4.000,00"

  Escenario: El PDF en COP no lleva la nota de TRM
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" genera el PDF del costeo de "C1"
    Entonces el PDF de "C1" no contiene "TRM mínima"
