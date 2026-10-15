# language: es
# HU-003.6 (#12, parte B) — Aislamiento por comercial en la API web (checklist de #12: "el comercial A no puede ver ni
# tocar cotizaciones del comercial B"). Se ejecuta por HTTP contra la API real en memoria (WebApplicationFactory) con
# sesiones emitidas por POST /api/auth/login-clientepro y SQL Server 2019 en Testcontainers.
# Los nombres de comerciales, regionales y cotizaciones son alias: cada escenario crea los suyos.
@integracion
Característica: Aislamiento de las cotizaciones por comercial
  Como comercial de Botero Soto
  Quiero que mis cotizaciones solo las vea y las toque yo (y mi gerente comercial)
  Para que ningún otro comercial vea precios, clientes ni correos que no le corresponden

  Antecedentes:
    # Con el correo habilitado el envío llega hasta la regla de dueño (deshabilitado respondería 503 antes); el relay de
    # pruebas no se alcanza nunca porque ningún envío de estos escenarios es de una cotización propia.
    Dado la API web de Cotifly con el envío de correo habilitado
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y el gerente comercial "hugo" con sesión en la regional "Norte"
    Y que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF
    Y que "luis" tiene la cotización web "B1" del cliente "800333444" con su PDF

  Escenario: Cada comercial solo ve sus cotizaciones en el listado
    Cuando "ana" consulta sus cotizaciones por la API
    Entonces la respuesta tiene el estado 200
    Y el listado incluye la cotización "A1"
    Y el listado no incluye la cotización "B1"

  Esquema del escenario: Una cotización ajena responde lo mismo que una inexistente
    Cuando "ana" intenta <operación> la cotización "B1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Y intentar <operación> una cotización inexistente da exactamente la misma respuesta
    Y la cotización "B1" sigue sin enviar y sin seguimiento

    Ejemplos:
      | operación                   |
      | marcar el seguimiento de    |
      | enviar por correo           |
      | previsualizar el correo de  |
      | pedir una versión nueva de  |
      | descargar el PDF de         |

  Escenario: Los destinatarios previos de un cliente solo incluyen los envíos propios
    Dado que "luis" ya envió la cotización "B1" a "compras@cliente-b.com"
    Cuando "ana" consulta por la API los destinatarios previos del cliente "800333444"
    Entonces la respuesta tiene el estado 200
    Y los destinatarios previos están vacíos

  Escenario: El gerente comercial descarga el PDF de un comercial de su regional
    Cuando "gloria" descarga el PDF de la cotización "B1"
    Entonces la respuesta tiene el estado 200 con un PDF

  Escenario: Un gerente comercial de otra regional no descarga el PDF
    Cuando "hugo" descarga el PDF de la cotización "B1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
