# language: es
# HU-013.3 (#99, ADR-018) — Servicios conexos desde el catálogo de Nexo: GET /web/servicios-conexos y
# POST /web/cotizaciones/{folio}/costeo/servicios-conexos. Nexo es la fuente de verdad del costo (el conexo se agrega en
# Nexo con POST /api/cotizaciones/{id}/conexos) y Cotifly le aplica el margen. El catálogo nunca expone costoUnitario.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso (200, 400, 404, 403 y caída).
# @pendiente @ignore: escenario SDD de HU-013.1 (#97); Reqnroll los genera como omitidos hasta que #99 enlace los pasos y
# quite las dos etiquetas.
@integracion @pendiente @ignore
Característica: Servicios conexos del catálogo de Nexo
  Como comercial de Botero Soto
  Quiero buscar servicios conexos en el catálogo de Nexo y agregarlos a mi cotización
  Para cotizar el servicio completo con el costo real de Nexo y el margen de la cotización

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el catálogo de Nexo tiene estos servicios conexos:
      | id                                   | codigo | nombre         | categoria | unidad | costo_unitario | activo |
      | 6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d | ALM-01 | Almacenamiento | logistico | Día    | 120000         | true   |
      | 7a2d3b0f-4c8e-4d66-8b1f-2e3a4b5c6d7e | PRE-01 | Precinto       | adicional | Unidad | 35000          | true   |
      | 8b3e4c1a-5d9f-4e77-9c2a-3f4b5c6d7e8f | ESC-01 | Escolta        | logistico | Viaje  | 900000         | false  |

  # Catálogo
  Escenario: La búsqueda devuelve los servicios activos sin el costo de Nexo
    Cuando "ana" busca servicios conexos con el texto "a"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "PaginaServiciosConexos" del contrato
    Y la página trae 2 servicios con el total 2
    Y ninguna respuesta del catálogo contiene el campo "costo_unitario" ni el valor 120000
    Y Nexo recibió la consulta del catálogo con "soloActivos=true" y la identidad de "ana"

  Escenario: Filtro por categoría y paginación
    Cuando "ana" busca servicios conexos de la categoría "logistico" en la página 1 de 1
    Entonces la página trae 1 servicio con el total 1
    Y el primer servicio de la página es "Almacenamiento"

  Escenario: Una búsqueda sin coincidencias responde una página vacía
    Cuando "ana" busca servicios conexos con el texto "helicóptero"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "PaginaServiciosConexos" del contrato
    Y la página trae 0 servicios con el total 0

  Escenario: Con Nexo caído el catálogo no está disponible
    Dado que Nexo deja de responder
    Cuando "ana" busca servicios conexos con el texto "a"
    Entonces la respuesta tiene el estado 503 con el código de negocio "NEXO_NO_DISPONIBLE"

  Escenario: Si Nexo niega el permiso al comercial
    Dado que Nexo responde 403 al catálogo de servicios conexos
    Cuando "ana" busca servicios conexos con el texto "a"
    Entonces la respuesta tiene el estado 403 con el código de negocio "NEXO_SIN_PERMISO"

  # Agregar a la cotización
  Escenario: Agregar un conexo lo crea en Nexo, aplica el margen y lo deja incluido
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 2
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y Nexo recibió para la cotización de "C1" el conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 2 en el lineaId de la línea 1 de su cotización y sin valorAplicado
    Y el servicio conexo "Almacenamiento" de "C1" está incluido con el precio 282353
    Y ninguna respuesta del costeo de "C1" contiene el costo 240000

  Escenario: Varios conexos en una sola petición
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" agrega a "C1" estos servicios conexos:
      | linea_orden | servicio_id                          | cantidad |
      | 1           | 6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d | 1        |
      | 1           | 7a2d3b0f-4c8e-4d66-8b1f-2e3a4b5c6d7e | 4        |
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y "C1" tiene 2 servicios conexos incluidos

  Escenario: Una línea que no existe en la cotización es un conexo inválido y no llega a Nexo
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" agrega a "C1" en la línea 2 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1
    Entonces la respuesta tiene el estado 400 con el código de negocio "CONEXO_INVALIDO"
    Y Nexo no recibió conexos para la cotización de "C1"

  Escenario: Si Nexo niega el rol al agregar no cambia nada
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que Nexo responde 403 al agregar conexos porque "ana" no tiene el rol Comercial, CoordinadorFletes ni Admin
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1
    Entonces la respuesta tiene el estado 403 con el código de negocio "NEXO_SIN_PERMISO"
    Y "C1" tiene 0 servicios conexos incluidos

  Escenario: Un servicio que Nexo no reconoce es un conexo inválido y no cambia nada
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que Nexo responde 404 al agregar el conexo "9c4f5d2b-6e0a-4f88-8d3b-4a5b6c7d8e9f"
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "9c4f5d2b-6e0a-4f88-8d3b-4a5b6c7d8e9f" con la cantidad 1
    Entonces la respuesta tiene el estado 400 con el código de negocio "CONEXO_INVALIDO"
    Y "C1" tiene 0 servicios conexos incluidos

  Escenario: Un servicio inactivo responde conexo inválido
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que Nexo responde 400 al agregar el conexo "8b3e4c1a-5d9f-4e77-9c2a-3f4b5c6d7e8f"
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "8b3e4c1a-5d9f-4e77-9c2a-3f4b5c6d7e8f" con la cantidad 1
    Entonces la respuesta tiene el estado 400 con el código de negocio "CONEXO_INVALIDO"

  Esquema del escenario: Cantidades fuera de rango no llegan a Nexo
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad <cantidad>
    Entonces la respuesta tiene el estado 400
    Y Nexo no recibió conexos para la cotización de "C1"

    Ejemplos:
      | cantidad  |
      | 0         |
      | -1        |
      | 1000000   |

  Escenario: Con Nexo caído al agregar no cambia nada
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que Nexo deja de responder
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1
    Entonces la respuesta tiene el estado 503 con el código de negocio "NEXO_NO_DISPONIBLE"
    Y "C1" tiene 0 servicios conexos incluidos

  Escenario: Sin costeo todavía no se agregan conexos
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C2" y Nexo no lo ha costeado
    Cuando "ana" agrega a "C2" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_LISTO"

  Escenario: Con el PDF ya generado no se agregan conexos
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" generó el PDF del costeo de "C1"
    Cuando "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_GENERADO"
