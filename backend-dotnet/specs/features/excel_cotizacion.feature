# language: es
# HU-018.1 (#155, parte C) — Excel de la cotización: GET /web/cotizaciones/{folio}/excel, con ClosedXML como el de la licitación.
# El dueño recibe la versión interna (por ruta y por camión: costo, margen, precio, conexos y totales). El gerente de su regional y el
# director (HU-037) reciben el mismo Excel en solo lectura SIN ninguna columna de costo ni de margen (con el margen y el precio se deduce el
# costo; criterio de SinCostoFinal() de HU-039.1, #218). Los demás reciben 404. Respeta el precio congelado (HU-036) y las rutas
# rechazadas (HU-028.1). Por HTTP contra la API real en memoria, con sesiones del login SSO y SQL Server 2019 en Testcontainers.
# La cotización web de prueba tiene una ruta con un camión: flete de 1.000.000 (COP), margen de 15 % y precio de 1.176.471, con su PDF.
@integracion
Característica: Excel de una cotización
  Como comercial (o gerente comercial de su regional, o director)
  Quiero descargar en Excel el detalle de una cotización por ruta y por camión
  Para revisarlo o compartirlo sin copiar a mano lo que muestra "Ver detalle"

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y el gerente comercial "hugo" con sesión en la regional "Norte"
    Y que "ana" tiene la cotización web "A1" del cliente "900111222" con su PDF

  Escenario: El dueño descarga la versión interna con costos, márgenes y precios por ruta y por camión
    Cuando "ana" descarga el Excel de la cotización "A1"
    Entonces la respuesta es el Excel "A1.xlsx" con las hojas "Resumen", "Rutas", "Camiones" y "Conexos"
    Y el Excel es la versión "Interna (con costos y márgenes)"
    Y la hoja "Rutas" tiene las columnas "Costo bruto (COP)" y "Margen aplicado"
    Y la ruta 1 del Excel tiene el costo bruto 1000000, el margen de 15 % y el precio 1176471
    Y el camión 1 de la ruta 1 del Excel tiene el costo 1000000 y el precio 1176471
    Y el resumen del Excel dice que el precio está congelado

  Escenario: El gerente de la regional del dueño lo descarga sin ninguna columna de costo ni de margen
    Cuando "gloria" descarga el Excel de la cotización "A1"
    Entonces la respuesta es el Excel "A1.xlsx" con las hojas "Resumen", "Rutas", "Camiones" y "Conexos"
    Y el Excel es la versión "Solo lectura (sin costos ni márgenes)"
    Y ninguna hoja del Excel tiene una columna de costo ni de margen
    Y el Excel no contiene el costo 1000000 ni el margen de 15 %
    Y la ruta 1 del Excel tiene el precio 1176471

  # HU-037.3 (#211): el director descarga el de cualquier regional, también sin costos.
  Escenario: El director lo descarga de otra regional sin ninguna columna de costo ni de margen
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" descarga el Excel de la cotización "A1"
    Entonces la respuesta es el Excel "A1.xlsx" con las hojas "Resumen", "Rutas", "Camiones" y "Conexos"
    Y ninguna hoja del Excel tiene una columna de costo ni de margen
    Y el Excel no contiene el costo 1000000 ni el margen de 15 %

  Escenario: Un gerente de otra regional y un comercial ajeno no lo descargan
    Cuando "hugo" descarga el Excel de la cotización "A1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Cuando "luis" descarga el Excel de la cotización "A1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."

  Escenario: Sin precio el Excel todavía no se puede descargar
    Dado que la cotización "A1" todavía no tiene precio
    Cuando "ana" descarga el Excel de la cotización "A1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_LISTO"
