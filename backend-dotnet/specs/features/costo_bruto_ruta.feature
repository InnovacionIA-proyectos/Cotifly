# language: es
# HU-039.1 (#218) — Costo bruto de la ruta para el dueño: `rutas[].costo_final` en GET /web/cotizaciones/{folio}/costeo y en el
# detalle (GET /web/cotizaciones/{folio}). Reversa de la regla de ADR-015 §5 ("el costo bruto nunca llega al comercial"), confirmada
# por Sara (Gerencia Comercial) el 2026-10-27. El costo es el total de la ruta (fletes + retorno + levante + conexos, sin margen),
# siempre en COP, y `null` mientras la ruta no tenga precio. Solo el dueño: al gerente en solo consulta le sale `null`, y no sale por
# "Mis cotizaciones", el listado regional, la vista previa del correo ni el PDF. Contrato en specs/openapi.yaml (RutaCosteo).
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso. Margen de la política: 15 %, método
# "margen": precio = costo / (1 - margen). Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosCostoBrutoRuta.cs.
@integracion
Característica: Costo bruto de la ruta para el dueño de la cotización
  Como comercial dueño de una cotización
  Quiero ver el costo bruto que costeó Nexo para cada ruta junto a su precio
  Para saber cuánto margen real le queda a la ruta antes de negociar con el cliente

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Sin costeo de Nexo la ruta no trae costo ni precio
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1" y Nexo no lo ha costeado
    Cuando "ana" consulta el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la ruta 1 de la respuesta no trae costo final ni precio final

  Escenario: Con el costeo listo el dueño ve el costo bruto de la ruta junto a su precio
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" consulta el costeo de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la ruta 1 de la respuesta tiene el costo final 2550000 y el precio final 3000000

  # HU-043.2 (#251): el precio es 2.550.000 / (1 − 0,15) + 85.000 del conexo a su venta (aquí el costo), sin margen.
  Escenario: El costo bruto de la ruta incluye sus servicios conexos sin margen
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que el costeo de "C1" trae el servicio conexo "Cargue" con un valor de Nexo de 85000
    Cuando "ana" consulta el costeo de "C1"
    Entonces la ruta 1 de la respuesta tiene el costo final 2635000 y el precio final 3085000

  @trm
  Escenario: En una cotización en USD el costo bruto sigue en pesos
    Dado que "ana" envió a costeo en "USD" el formulario del cliente "900555111" como "U1" con la TRM 4000
    Y Nexo costeó la solicitud de "U1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" consulta el costeo de "U1"
    Entonces la ruta 1 de la respuesta tiene el costo final 2550000 y el precio final 750.00

  Escenario: El detalle muestra el costo bruto al dueño pero no al gerente que solo consulta
    Dado el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" consulta el detalle de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DetalleCotizacion" del contrato
    Y la ruta 1 del costeo del detalle tiene el costo final 2550000
    Cuando "gloria" consulta el detalle de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "DetalleCotizacion" del contrato
    Y el detalle es de solo consulta y la ruta 1 de su costeo no trae costo final
    Y la respuesta no contiene el costo 2550000

  Escenario: El costo bruto no sale por los demás caminos de la cotización
    Dado el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" generó el PDF del costeo de "C1"
    Cuando "ana" consulta sus cotizaciones por la API
    Entonces la respuesta no contiene el costo 2550000
    Cuando "gloria" consulta las cotizaciones de su regional
    Entonces la respuesta no contiene el costo 2550000
    Cuando "ana" previsualiza el correo del costeo de "C1"
    Entonces la respuesta no contiene el costo 2550000
    Y el PDF de "C1" no contiene "2.550.000"
    Y el PDF de "C1" no contiene "2550000"

  Escenario: La versión nueva muestra el costo bruto de su propio costeo y el ajuste de margen no lo cambia
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y que Nexo costeó la solicitud de "C1" con un flete de 2550000 y la entregó
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Nueva versión" y nueva_version "true"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la versión nueva de la respuesta queda registrada como "C1v2"
    Cuando "ana" consulta el costeo de "C1v2"
    Entonces la ruta 1 de la respuesta no trae costo final ni precio final
    Dado Nexo costeó la solicitud de "C1v2" con un flete de 2040000 y el polling lo registró
    Cuando "ana" consulta el costeo de "C1v2"
    Entonces la ruta 1 de la respuesta tiene el costo final 2040000 y el precio final 2400000
    Cuando "ana" consulta el costeo de "C1"
    Entonces la ruta 1 de la respuesta tiene el costo final 2550000 y el precio final 3000000
    Cuando "ana" ajusta el margen de "C1v2" a "0.25" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y la ruta 1 de la respuesta tiene el costo final 2040000 y el precio final 2720000
