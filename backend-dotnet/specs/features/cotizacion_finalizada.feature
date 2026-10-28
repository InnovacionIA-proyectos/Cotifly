# language: es
# HU-036 (milestone "Cotización finalizada") — El comercial acepta el precio y FINALIZA la cotización.
# HU-036.2 (#206): POST /web/cotizaciones/{folio}/finalizar con If-Match (solo el dueño). Solo con el costeo de Nexo listo
# (autocosteo o entregada): si no, 409 COTIZACION_NO_FINALIZABLE. Re-finalizar es 200 sin cambios. If-Match viejo: 409
# CONFLICTO_CONCURRENCIA. La finalización va en columnas propias (finalizadaAt, finalizadaPor; db/scripts/14), no en estado_costeo, y se
# expone como finalizada_at en CotizacionResumen, CosteoCotizacion y GET /web/cotizaciones/{folio}.
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
