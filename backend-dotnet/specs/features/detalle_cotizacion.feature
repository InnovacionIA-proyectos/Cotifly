# language: es
# HU-027.1 (#179, parte A) — Detalle por folio: GET /web/cotizaciones/{folio}. "Ver detalle" (HU-021-FE.7, #75) en una sola llamada.
# Por HTTP contra la API real en memoria, con sesiones del login SSO y SQL Server 2019 en Testcontainers. La regional se fija en
# UsuariosWeb.regional. Los nombres de comerciales, regionales y cotizaciones son alias: cada escenario crea los suyos.
@integracion
Característica: Detalle de una cotización por folio
  Como comercial (o gerente comercial de su regional)
  Quiero ver en una sola llamada la cabecera, el costeo por ruta, las rutas capturadas y los totales de una cotización
  Para pintar y recargar "Ver detalle" sin combinar varios endpoints

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y el gerente comercial "hugo" con sesión en la regional "Norte"
    Y que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF

  Escenario: El dueño ve el detalle completo de su cotización
    Cuando "ana" consulta el detalle de la cotización "A1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DetalleCotizacion" del contrato
    Y el detalle es de la cotización "A1", su dueño es "ana" y no es de solo consulta
    Y el detalle trae el ETag igual a su row_version

  Escenario: El gerente de la regional del dueño lo ve en solo consulta
    Cuando "gloria" consulta el detalle de la cotización "A1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DetalleCotizacion" del contrato
    Y el detalle es de la cotización "A1", su dueño es "ana" y es de solo consulta

  # HU-037.3 (#211): el director ve el detalle de cualquier regional, en solo consulta.
  Escenario: El director ve en solo consulta el detalle de una cotización de otra regional
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta el detalle de la cotización "A1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DetalleCotizacion" del contrato
    Y el detalle es de la cotización "A1", su dueño es "ana" y es de solo consulta

  Escenario: Un gerente de otra regional y un comercial ajeno no la ven
    Cuando "hugo" consulta el detalle de la cotización "A1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Cuando "luis" consulta el detalle de la cotización "A1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
