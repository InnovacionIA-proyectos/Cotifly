# language: es
# HU-013.2 (#98, ADR-018) — Ajuste de margen en el flujo de costeo: PUT /web/cotizaciones/{folio}/costeo/margen.
# Contrato en specs/openapi.yaml (AjusteMargenWeb, CosteoCotizacion). Por HTTP contra la API real en memoria, SQL Server 2019
# en Testcontainers y un Nexo falso. Margen sugerido de la cotización: 15 % (política de la línea), método "margen":
# precio = costo / (1 - margen), a pesos enteros. Los pasos de mutación envían If-Match con el ETag vigente salvo que el
# escenario diga otra cosa (ADR-018 §8). Regla del PDF: ADR-018 §11.
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosAjusteMargen.cs (#98); los de la moneda del escenario en USD, en
# clsPasosMonedaCosteo.cs (#103, HU-013.7).
@integracion
Característica: Ajuste de margen sobre el costeo de Nexo
  Como comercial de Botero Soto
  Quiero ajustar el margen de una cotización ya costeada
  Para negociar con el cliente sin salir del flujo determinista de precio

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró

  Escenario: Subir el margen por encima del sugerido no exige justificación y recalcula el precio
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 3109756 con el margen aplicado "0.18" y el origen "negociado"
    Y el margen sugerido de "C1" sigue en "0.15"
    Y queda un ajuste de margen de "C1" de "0.15" a "0.18" por "ana" sin justificación

  Escenario: Bajar el margen por debajo del sugerido con justificación
    Cuando "ana" ajusta el margen de "C1" a "0.12" con la justificación "Cliente estratégico; la competencia cotizó más bajo"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 2897727 con el margen aplicado "0.12" y el origen "negociado"
    Y queda un ajuste de margen de "C1" de "0.15" a "0.12" por "ana" con la justificación "Cliente estratégico; la competencia cotizó más bajo"

  Escenario: Bajar el margen por debajo del sugerido sin justificación se rechaza
    Cuando "ana" ajusta el margen de "C1" a "0.10" sin justificación
    Entonces la respuesta tiene el estado 400 con el código de negocio "JUSTIFICACION_REQUERIDA"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"
    Y no hay ajustes de margen de "C1"

  Escenario: Una justificación en blanco cuenta como ausente
    Cuando "ana" ajusta el margen de "C1" a "0.10" con la justificación "   "
    Entonces la respuesta tiene el estado 400 con el código de negocio "JUSTIFICACION_REQUERIDA"

  Esquema del escenario: Los bordes del rango son válidos
    Cuando "ana" ajusta el margen de "C1" a "<margen>" con la justificación "Prueba de borde"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final <precio> con el margen aplicado "<margen>" y el origen "negociado"

    Ejemplos:
      | margen | precio    |
      | 0      | 2550000   |
      | 0.99   | 255000000 |

  Esquema del escenario: Un margen fuera de 0 a 0,99 se rechaza sin cambios
    Cuando "ana" ajusta el margen de "C1" a "<margen>" con la justificación "Fuera de rango"
    Entonces la respuesta tiene el estado 400 con el código de negocio "AJUSTE_MARGEN_INVALIDO"
    Y no hay ajustes de margen de "C1"

    Ejemplos:
      | margen |
      | -0.01  |
      | 1      |
      | 1.5    |

  Escenario: Ajustar al mismo margen vigente no registra un ajuste nuevo
    Cuando "ana" ajusta el margen de "C1" a "0.15" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y no hay ajustes de margen de "C1"

  Escenario: Los servicios conexos incluidos se recalculan con el nuevo margen
    Dado que el costeo de "C1" trae el servicio conexo "Cargue" con un valor de Nexo de 85000
    Cuando "ana" ajusta el margen de "C1" a "0.20" sin justificación
    Entonces el servicio conexo "Cargue" de "C1" tiene el precio 106250
    Y ninguna respuesta del costeo de "C1" contiene el costo 85000

  # HU-013.7 (#103): 2.550.000 / (1 - 0,18) = 3.109.756 pesos / 4.000 = 777,44 USD, con la TRM fijada al crear y no la del día.
  @trm
  Escenario: En USD el ajuste usa la TRM ya fijada, nunca una nueva
    Dado que "ana" envió a costeo en "USD" el formulario del cliente "900555111" como "U1" con la TRM 4000
    Y Nexo costeó la solicitud de "U1" con un flete de 2550000 y el polling lo registró
    Y la TRM del día cambia a 4200
    Cuando "ana" ajusta el margen de "U1" a "0.18" sin justificación
    Entonces el costeo de "U1" tiene el precio final 777.44 en "USD" con la TRM aplicada 4000

  Escenario: Sin costeo todavía no se puede ajustar
    Dado que "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C2" y Nexo no lo ha costeado
    Cuando "ana" ajusta el margen de "C2" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_LISTO"

  # HU-036.3 (#207): una cotización finalizada ya no admite el ajuste (cotizacion_finalizada.feature). Este caso queda para las
  # cotizaciones anteriores a HU-036 (sin backfill): PDF generado sin finalizar. Al regenerar el PDF, el paso finaliza primero.
  Escenario: Con el PDF generado antes de HU-036 y sin enviar el ajuste invalida el PDF y se regenera en el mismo folio
    Dado que "ana" generó el PDF del costeo de "C1" antes de HU-036, sin finalizarla
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" no tiene pdf_url y está marcado con pdf_desactualizado
    Y el PDF anterior de "C1" queda en el historial de auditoría
    Cuando "ana" genera el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato
    Y el costeo de "C1" tiene pdf_url y ya no está marcado con pdf_desactualizado

  Escenario: Ya enviada al cliente no se ajusta y se indica crear una versión
    Dado que "ana" generó el PDF del costeo de "C1" y lo envió al cliente
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"
    Y el detalle del error indica la acción "nueva_version" con "POST" al endpoint de re-solicitud de "C1" y nueva_version "true"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"

  Escenario: Con resultado registrado no se ajusta y se indica crear una versión
    Dado que Nexo entregó la cotización de "C1" y "ana" la marcó como "ganada"
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"

  Escenario: Sin If-Match se exige la precondición
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación y sin If-Match
    Entonces la respuesta tiene el estado 428 con el código de negocio "PRECONDICION_REQUERIDA"
    Y no hay ajustes de margen de "C1"

  Escenario: La respuesta del ajuste trae el ETag nuevo
    Dado que "ana" leyó el costeo de "C1" y guardó su ETag
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación con el ETag guardado
    Entonces la respuesta trae un ETag distinto del guardado

  Escenario: Otro comercial no ve la cotización
    Dado el comercial "beto" con sesión en la regional "Occidente"
    Cuando "beto" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 404

  Escenario: Un If-Match desactualizado responde conflicto y no cambia nada
    Dado que "ana" leyó el costeo de "C1" y guardó su ETag
    Y "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Cuando "ana" ajusta el margen de "C1" a "0.20" sin justificación con el ETag guardado
    Entonces la respuesta tiene el estado 409 con el código de negocio "CONFLICTO_CONCURRENCIA"
    Y el costeo de "C1" tiene el precio final 3109756 con el margen aplicado "0.18" y el origen "negociado"
