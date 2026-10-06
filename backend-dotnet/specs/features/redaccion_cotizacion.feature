# language: es
# HU-013.5 (#101, ADR-018) — Redactor IA del asunto y la introducción del PDF con el texto estándar de respaldo:
# POST /web/cotizaciones/{folio}/costeo/textos/redactar y PUT /web/cotizaciones/{folio}/costeo/textos. La IA nunca recibe
# costos, margen ni precios; su salida se valida (sin montos, URL ni correos; asunto <= 120 y introducción <= 900
# caracteres) y, si falla o tarda más de 15 s, se usa clsTextosEstandar. El PDF siempre sale. Con un IChatClient guionizado
# (nunca un proveedor real), la API real en memoria y SQL Server 2019 en Testcontainers. Pasos en
# tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosRedaccionCotizacion.cs. El formulario de los antecedentes es el de la línea
# "Importacion" (política del 15 %: el flete de 2550000 queda en un precio de 3000000).
@integracion
Característica: Redacción del asunto y la introducción de la cotización
  Como comercial de Botero Soto
  Quiero que la IA me proponga el asunto y la introducción del PDF y poder editarlos
  Para entregar una cotización bien redactada sin que la IA toque los números

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró

  Escenario: La IA redacta y los textos quedan guardados con fuente ia
    Dado que el modelo responde el asunto "Cotización transporte Buenaventura - Bogotá" y una introducción válida
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y los textos de "C1" tienen la fuente "ia" y no están editados
    Y el asunto de "C1" es "Cotización transporte Buenaventura - Bogotá"
    Y la redacción de "C1" queda en la auditoría con el proveedor, el modelo, la duración y sin respaldo

  Escenario: La IA nunca recibe costos, margen ni precios
    Dado que el modelo responde el asunto "Cotización transporte Buenaventura - Bogotá" y una introducción válida
    Cuando "ana" pide redactar los textos de "C1"
    Entonces el mensaje enviado al modelo menciona el cliente, la línea "Importacion", "Buenaventura", "Bogotá D.C." y la moneda "COP"
    Y el mensaje enviado al modelo no contiene "2550000", "3000000" ni "0.15"

  Escenario: Si la IA tarda más de 15 segundos se usa el texto estándar
    Dado que el modelo tarda 16 segundos en responder
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y los textos de "C1" tienen la fuente "estandar" y no están editados
    Y la redacción de "C1" queda en la auditoría con respaldo

  Escenario: Si la IA está caída se usa el texto estándar
    Dado que el modelo falla con un error 503
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y los textos de "C1" tienen la fuente "estandar" y no están editados

  Esquema del escenario: Una salida inválida de la IA se descarta
    Dado que el modelo responde <salida>
    Cuando "ana" pide redactar los textos de "C1"
    Entonces los textos de "C1" tienen la fuente "estandar" y no están editados

    Ejemplos:
      | salida                                                         |
      | una introducción que menciona "$ 3.000.000"                    |
      | una introducción con la URL "https://ejemplo.com"              |
      | una introducción con el correo "alguien@ejemplo.com"           |
      | un asunto de 121 caracteres                                    |
      | una introducción de 901 caracteres                             |
      | un JSON que no cumple el esquema del redactor                  |

  Escenario: El comercial edita los textos
    Dado que el modelo responde el asunto "Cotización transporte Buenaventura - Bogotá" y una introducción válida
    Y "ana" pidió redactar los textos de "C1"
    Cuando "ana" guarda en "C1" el asunto "Propuesta de transporte para Cliente SAS" y la introducción "Apreciado cliente: con gusto le presentamos nuestra propuesta."
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y los textos de "C1" tienen la fuente "ia" y están editados

  Esquema del escenario: Textos del comercial inválidos se rechazan
    Cuando "ana" guarda en "C1" el asunto "<asunto>" y la introducción "<introduccion>"
    Entonces la respuesta tiene el estado 400 con el código de negocio "TEXTOS_COTIZACION_INVALIDOS"

    Ejemplos:
      | asunto                  | introduccion                                  |
      |                         | Introducción válida                           |
      | Asunto válido           |                                               |
      | Asunto válido           | Visite https://ejemplo.com para más detalles  |
      | Escriba a a@ejemplo.com | Introducción válida                           |

  Escenario: El PDF imprime los textos guardados
    Dado que "ana" guardó en "C1" el asunto "Propuesta de transporte para Cliente SAS" y una introducción válida
    Cuando "ana" genera el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato
    Y el PDF de "C1" contiene "Propuesta de transporte para Cliente SAS"

  Escenario: Sin textos guardados el PDF sale con los estándar y sin llamar a la IA
    Dado que el modelo falla con un error 503
    Cuando "ana" genera el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato
    Y el modelo no fue llamado
    Y el PDF de "C1" contiene la introducción estándar

  Escenario: Con el PDF generado y sin enviar, editar los textos invalida el PDF
    Dado que "ana" generó el PDF del costeo de "C1"
    Cuando "ana" guarda en "C1" el asunto "Propuesta revisada" y una introducción válida
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y el costeo de "C1" no tiene pdf_url y está marcado con pdf_desactualizado

  Escenario: Ya enviada al cliente no se cambian los textos
    Dado que "ana" generó el PDF del costeo de "C1" y lo envió al cliente
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"
    Y el modelo no fue llamado

  Escenario: Redactar sobre textos editados por el comercial exige confirmación
    Dado que "ana" guardó en "C1" el asunto "Mi asunto" y una introducción válida
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "TEXTOS_EDITADOS"
    Y el modelo no fue llamado
    Y el asunto de "C1" es "Mi asunto"

  Escenario: Con sobrescribir la IA reemplaza los textos editados
    Dado que "ana" guardó en "C1" el asunto "Mi asunto" y una introducción válida
    Y que el modelo responde el asunto "Cotización transporte Buenaventura - Bogotá" y una introducción válida
    Cuando "ana" pide redactar los textos de "C1" con sobrescribir
    Entonces la respuesta tiene el estado 200 y cumple el esquema "TextosCotizacion" del contrato
    Y los textos de "C1" tienen la fuente "ia" y no están editados

  Escenario: Sin If-Match se exige la precondición
    Cuando "ana" guarda en "C1" el asunto "Propuesta" y una introducción válida sin If-Match
    Entonces la respuesta tiene el estado 428 con el código de negocio "PRECONDICION_REQUERIDA"

  Escenario: Un If-Match desactualizado al editar responde conflicto
    Dado que "ana" leyó el costeo de "C1" y guardó su ETag
    Y "ana" guardó en "C1" el asunto "Primera versión" y una introducción válida
    Cuando "ana" guarda en "C1" el asunto "Segunda versión" y una introducción válida con el ETag guardado
    Entonces la respuesta tiene el estado 409 con el código de negocio "CONFLICTO_CONCURRENCIA"
