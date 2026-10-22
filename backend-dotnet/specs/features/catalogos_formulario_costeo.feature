# language: es
# HU-013.1 (#97, ADR-018), HU-013.10 (#106) y docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md — Catálogos del formulario de costeo. POST
# /api/captura-solicitudes de Nexo no valida contra sus catálogos: resuelve el texto por coincidencia aproximada y, si no
# encuentra el tipo de vehículo, crea una fila nueva de catálogo (duplicados). Cotifly ofrece las opciones de los catálogos
# maestros de Nexo (GET /api/catalogos/*, con caché) en lugar del histórico de tarifas (biblioteca-sap-historico/filtros) y
# envía a Nexo solo códigos de catálogo. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un
# Nexo falso. Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosCatalogosCosteo.cs.
@integracion
Característica: Solo códigos de catálogo en el formulario de costeo
  Como comercial de Botero Soto
  Quiero elegir el vehículo y la clase de viaje de los catálogos maestros de Nexo
  Para que Nexo costee la ruta correcta sin crear vehículos duplicados

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el catálogo maestro de tipos de vehículo de Nexo tiene:
      | codigo | descripcion                     | esCatalogoOficial |
      | 2S3    | Tractocamión 2 ejes, semi 3     | true              |
      | 3S3    | Tractocamión 3 ejes, semi 3     | true              |
    Y el catálogo maestro de clases de viaje de Nexo tiene "Contenedor", "Cupo", "Carga Suelta" y "Extradimensionado"

  Escenario: Los catálogos del formulario salen de los catálogos maestros de Nexo
    Cuando "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CatalogosFlete" del contrato
    Y los tipos de vehículo son "2S3" y "3S3" con su descripción como etiqueta
    Y las clases de viaje son "Contenedor", "Cupo", "Carga Suelta" y "Extradimensionado"
    Y Nexo recibió la consulta de "/api/catalogos/tipos-vehiculo" y de "/api/catalogos/clases-viaje"
    Y Nexo no recibió la consulta de "/api/biblioteca-sap-historico/filtros"

  # HU-027.1 (#179): en Nexo la descripcion de clases-contenedor es el código (CNT40.HC); es el valor que valida la captura.
  Escenario: Los catálogos de flete exponen las clases de contenedor de Nexo sin la lista provisional
    Cuando "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CatalogosFlete" del contrato
    Y las clases de contenedor son "CNT20.IT", "CNT20.RE", "CNT20.ST", "CNT40.HC" y "CNT40.RE" con el mismo texto como etiqueta
    Y las clases de contenedor no incluyen las provisionales "CNT20.FT", "CNT20.OP", "CNT40.FT" ni "CNT40.OP"
    Y Nexo recibió la consulta de "/api/catalogos/clases-contenedor" y de "/api/catalogos/clases-viaje"

  Esquema del escenario: Cada clase de contenedor de los catálogos de flete se acepta en la captura y viaja a Nexo tal cual
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Contenedor" y la clase de contenedor "<clase>"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió la solicitud de "C1" con el claseContenedor "<clase>"

    Ejemplos:
      | clase    |
      | CNT20.IT |
      | CNT20.RE |
      | CNT20.ST |
      | CNT40.HC |
      | CNT40.RE |

  Escenario: Si Nexo no tiene clases de contenedor los catálogos de flete responden con la lista vacía
    Dado que el catálogo maestro de clases de contenedor de Nexo está vacío
    Cuando "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CatalogosFlete" del contrato
    Y las clases de contenedor vienen vacías
    Y las clases de viaje son "Contenedor", "Cupo", "Carga Suelta" y "Extradimensionado"

  Escenario: Si falla el catálogo de clases de contenedor los catálogos de flete responden con la lista vacía
    Dado que el catálogo maestro de clases de contenedor de Nexo falla
    Cuando "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CatalogosFlete" del contrato
    Y las clases de contenedor vienen vacías

  Escenario: Una clase de viaje del catálogo viaja a Nexo con su código tal cual
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Contenedor"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió la solicitud de "C1" con el claseViaje "Contenedor"

  # HU-021.6 (decisión 7 de HU-021): el comercial ya no digita vehículo ni carrocería; los define el cubicador o la Mesa de Costeo.
  # HU-014 (#188, decisión del 2026-10-19): la bandera de respaldo Captura:AceptarVehiculoCliente está encendida por defecto, así que el
  # vehículo que llega se valida contra el catálogo de Nexo (va solo en el camión del respaldo) y un texto libre se rechaza.
  Escenario: Un tipo de vehículo que no es código del catálogo se rechaza y no llega a Nexo
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con el tipo de vehículo "Tractomula"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el mensaje del error menciona "tipo_vehiculo"
    Y Nexo no recibió solicitudes

  Esquema del escenario: Una clase de viaje que no es código de catálogo se rechaza y no llega a Nexo
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "<texto>"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el mensaje del error menciona "clase_viaje"
    Y Nexo no recibió solicitudes
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

    Ejemplos:
      | texto                  |
      | Barco                  |
      | contenedor             |
      | Contenedor refrigerado |

  Escenario: Una tipología fuera del enum de Nexo se rechaza
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la tipología "Mercancía general"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y Nexo no recibió solicitudes

  Escenario: Clase de contenedor sin clase de viaje Contenedor se rechaza
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Carga Suelta" y la clase de contenedor "CNT40.HC"
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y Nexo no recibió solicitudes

  Escenario: Con el catálogo maestro caído el formulario no ofrece texto libre
    Cuando Nexo deja de responder
    Y "ana" pide los catálogos de flete
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"

  Escenario: Con el catálogo maestro caído y sin copia en memoria la solicitud no se guarda ni llega a Nexo
    Cuando Nexo deja de responder
    Y "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Contenedor"
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"
    Y "ana" no tiene cotizaciones en "Mis cotizaciones"

  Esquema del escenario: Las clases de contenedor provisionales de Nexo no se rechazan
    Cuando "ana" envía a costeo el formulario del cliente "900555111" como "C1" con la clase de viaje "Contenedor" y la clase de contenedor "<clase>"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y Nexo recibió la solicitud de "C1" con el claseContenedor "<clase>"

    Ejemplos:
      | clase    |
      | CNT20.FT |
      | CNT20.OP |
      | CNT40.FT |
      | CNT40.OP |

  # HU-031.1 (#194, NEXO #321): GET /web/alias/resolver traduce el texto del comercial (o del pliego) al código oficial con el diccionario de
  # alias de Nexo (GET /api/catalogos/alias). Si el diccionario no responde, la respuesta es "no disponible", nunca "no reconocido".
  Escenario: Un alias de vehículo del diccionario de Nexo se resuelve al código oficial del catálogo
    Dado que el diccionario de alias de Nexo traduce el alias "TB" al vehículo "2S3"
    Cuando "ana" resuelve el texto "tb" como tipo de vehículo
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AliasResueltos" del contrato
    Y el texto "tb" se reconoce como el código "2S3" por "alias"

  Escenario: Con el diccionario de alias caído la resolución responde no disponible y no "no reconocido"
    Dado que el diccionario de alias de Nexo traduce el alias "TB" al vehículo "2S3"
    Y que el diccionario de alias de Nexo falla
    Cuando "ana" resuelve el texto "TB" como tipo de vehículo
    Entonces la respuesta tiene el estado 503 con el código de negocio "CATALOGOS_FLETE_NO_DISPONIBLE"
