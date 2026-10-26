# language: es
# HU-036 (milestone "Cotización finalizada") — El comercial acepta el precio y FINALIZA la cotización.
# HU-036.2 (#206): POST /web/cotizaciones/{folio}/finalizar con If-Match (solo el dueño). Solo con el costeo de Nexo listo
# (autocosteo o entregada): si no, 409 COTIZACION_NO_FINALIZABLE. Re-finalizar es 200 sin cambios. If-Match viejo: 409
# CONFLICTO_CONCURRENCIA. La finalización va en columnas propias (finalizadaAt, finalizadaPor; db/scripts/14), no en estado_costeo, y se
# expone como finalizada_at en CotizacionResumen, CosteoCotizacion y GET /web/cotizaciones/{folio}.
# HU-036.3 (#207): candados. Después de finalizar, lo que cambia el precio (margen, conexos, conexos en Nexo, re-solicitud en el mismo
# folio, recálculo de camiones) responde 409 PDF_YA_ENVIADO con la llamada para crear la versión nueva, y la edición pasa a
# nueva_version. Antes de finalizar, el PDF, el correo, los textos del PDF y el resultado (con el costeo listo) responden 409
# COTIZACION_NO_FINALIZADA ("Finaliza la cotización antes de..."). La perdida antes del costeo sigue sin exigir finalizar.
# HU-036.4 (#208): con la cotización finalizada el polling no cambia el precio guardado: lo audita (costeo_nexo_precio_congelado) y lo
# advierte en motivo_estado_costeo con la ruta que cambió; el estado_costeo sigue avanzando con Nexo (mismo mecanismo que el PDF generado,
# ADR-015 §5).
# Contrato en specs/openapi.yaml. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers (con el script 14) y el Nexo
# falso (nunca el real). Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosFinalizacion.cs.
@integracion
Característica: Cotización finalizada
  Como comercial de Botero Soto
  Quiero finalizar la cotización cuando acepto su precio
  Para que el precio no cambie mientras genero el PDF, la envío al cliente y registro el resultado

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"

  # ------------------------------------------------------------------ HU-036.2 (#206): el endpoint

  Escenario: Con el costeo listo la cotización se finaliza y lo muestran el resumen, el costeo y el detalle
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" finaliza la cotización "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae finalizada_at y un ETag nuevo
    Y la cotización "C1" queda finalizada por "ana"
    Y el costeo y el detalle de "C1" traen finalizada_at
    Y la finalización de "C1" queda en la auditoría como exitosa

  Escenario: Volver a finalizar no cambia nada
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando "ana" finaliza la cotización "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el mismo finalizada_at y el mismo ETag de la primera finalización

  Escenario: Sin el costeo de Nexo listo no se finaliza
    Cuando "ana" finaliza la cotización "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_FINALIZABLE"
    Y el detalle del error trae el estado de costeo "pendiente_costeo"
    Y la cotización "C1" sigue sin finalizar
    Y la finalización de "C1" queda en la auditoría como rechazada por "COTIZACION_NO_FINALIZABLE"

  Escenario: Sin If-Match se exige la precondición
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" finaliza la cotización "C1" sin If-Match
    Entonces la respuesta tiene el estado 428 con el código de negocio "PRECONDICION_REQUERIDA"
    Y la cotización "C1" sigue sin finalizar

  Escenario: Si el polling cambió el precio después de que el comercial lo leyó, la finalización es un conflicto
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" leyó el costeo de "C1" y guardó su ETag
    Y Nexo cambia el flete de "C1" a 2800000 y el polling lo registró
    Cuando "ana" finaliza la cotización "C1" con el ETag guardado
    Entonces la respuesta tiene el estado 409 con el código de negocio "CONFLICTO_CONCURRENCIA"
    Y la cotización "C1" sigue sin finalizar

  Escenario: Otro comercial no puede finalizarla
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y el comercial "luis" con sesión en la regional "Occidente"
    Cuando "luis" finaliza la cotización "C1"
    Entonces la respuesta tiene el estado 404
    Y la cotización "C1" sigue sin finalizar

  Escenario: La cotización finalizada sigue avanzando con Nexo
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando Nexo entrega la cotización de "C1"
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "entregada"
    Y la cotización "C1" queda finalizada por "ana"

  # ------------------------------------------------------------------ HU-036.3 (#207): candados después de finalizar

  Escenario: Finalizada no admite cambios de margen y se indica crear una versión nueva
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"
    Y el detalle del error indica la acción "nueva_version" con "POST" al endpoint de re-solicitud de "C1" y nueva_version "true"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"

  Escenario: Finalizada no se re-solicita en el mismo folio pero sí en una versión nueva
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Actualizar el costo"
    Entonces la respuesta tiene el estado 409 con el código de negocio "PDF_YA_ENVIADO"
    Cuando "ana" re-solicita el costeo de "C1" con el motivo "Actualizar el costo" y nueva_version "true"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la respuesta trae el folio de "C1" con el sufijo "-v2" y la versión 2
    Y la cotización "C1" queda finalizada por "ana"

  Escenario: Finalizada, editar la solicitud crea una versión nueva
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando "ana" consulta la solicitud "C1" para editarla
    Entonces la solicitud "C1" se edita como "nueva_version"

  # ------------------------------------------------------------------ HU-036.3 (#207): candados antes de finalizar

  Escenario: Sin finalizar no se genera el PDF y después de finalizar sí
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" pide directamente el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_FINALIZADA"
    Y el error pide finalizar la cotización "C1" antes de "generar el PDF"
    Cuando "ana" finaliza la cotización "C1"
    Y "ana" pide directamente el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato

  Escenario: Sin finalizar no se redactan los textos del PDF
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" pide redactar los textos de "C1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_FINALIZADA"
    Y el error pide finalizar la cotización "C1" antes de "redactar los textos del PDF"

  Escenario: Con el costeo listo sin finalizar no se marca el resultado ni se llama a Nexo
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" pide directamente marcar el resultado de "C1" como "ganada"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COTIZACION_NO_FINALIZADA"
    Y el error pide finalizar la cotización "C1" antes de "marcar el resultado"
    Y la cotización "C1" sigue sin resultado
    Y Nexo no recibió ningún resultado de "C1"

  Escenario: Antes del costeo la perdida solo en Cotifly no exige finalizar
    Cuando "ana" marca "C1" como perdida porque "El cliente desistió"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CotizacionResumen" del contrato
    Y la cotización "C1" queda ganada "false" con el motivo "El cliente desistió"
    Y la cotización "C1" sigue sin finalizar

  # ------------------------------------------------------------------ HU-036.4 (#208): precio congelado frente al polling

  Escenario: Si la Mesa entrega con otro valor después de finalizar, el precio se conserva y se avisa qué ruta cambió
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que "ana" finalizó la cotización "C1"
    Cuando Nexo cambia el flete de "C1" a 2800000 y entrega la cotización
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "entregada"
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"
    Y el motivo del costeo de "C1" avisa que cambió la ruta 1
    Y el costo de 2800000 que trajo Nexo para "C1" queda en la auditoría
    Y ninguna respuesta del costeo de "C1" contiene el costo 2800000

  Escenario: Sin finalizar, el mismo cambio de Nexo sí recalcula el precio
    Dado Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando Nexo cambia el flete de "C1" a 2800000 y entrega la cotización
    Y el polling consulta a Nexo
    Entonces la cotización "C1" queda en el estado de costeo "entregada"
    Y el costeo de "C1" tiene el precio final 3294118 con el margen aplicado "0.15" y el origen "politica_linea"
