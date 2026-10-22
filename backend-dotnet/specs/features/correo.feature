# language: es
# HU-003.5 (#11) y HU-033 (#199, ADR-023) — Envío de la cotización por correo por el microservicio interno MailSender.
# Se ejecuta en Cotifly.Aceptacion.Tests con el registro real y un MailSender FALSO en proceso (sin red: ningún correo real sale).
# MailSender encola el correo (202 con su id) y lo entrega en segundo plano; el worker de correos de Cotifly consulta su estado.
# Fuera de Production todo correo va al buzón de pruebas con [QA] en el asunto y los destinatarios originales en el cuerpo.
# Cada escenario usa un folio distinto.
@integracion
Característica: Envío de la cotización por correo
  Como comercial de Botero Soto
  Quiero enviar la cotización al cliente con el PDF adjunto desde mi correo
  Para que el cliente la reciba, yo quede en copia oculta y la trazabilidad quede en la base

  Antecedentes:
    Dado el comercial "ana.correo" con nombre "Ana Correo" y correo "ana.correo@boterosoto.com.co"
    Y el comercial "luis.correo" con nombre "Luis Correo" y correo "luis.correo@boterosoto.com.co"
    Y el envío de correo habilitado con el MailSender de pruebas

  Escenario: Fuera de producción el correo se redirige al buzón de pruebas y queda encolado
    Dado que "ana.correo" tiene la cotización "PCN-01102026" del cliente "900123456" con su PDF
    Cuando "ana.correo" envía la cotización "PCN-01102026" a "cliente@empresa.com" con copia a "jefe@empresa.com"
    Entonces el envío es exitoso
    Y MailSender recibe el correo solo para el buzón de pruebas con el asunto "[QA] Cotización PCN-01102026 — Botero Soto"
    Y el cuerpo del correo lista los destinatarios originales "cliente@empresa.com", "jefe@empresa.com" y "ana.correo@boterosoto.com.co"
    Y el correo adjunta "PCN-01102026.pdf"
    Y el último correo de "PCN-01102026" queda registrado como "encolado" para "cliente@empresa.com" y redirigido al buzón de pruebas
    Y la cotización "PCN-01102026" queda en estado de envío "enviada"

  Escenario: En producción el correo sale al cliente con copia oculta al comercial
    Dado el ambiente de producción sin protección de correo
    Y que "ana.correo" tiene la cotización "PCN-12102026" del cliente "900123456" con su PDF
    Cuando "ana.correo" envía la cotización "PCN-12102026" a "cliente@empresa.com" con copia a "jefe@empresa.com"
    Entonces el envío es exitoso
    Y MailSender recibe el correo de "ana.correo@boterosoto.com.co" para "cliente@empresa.com" con copia a "jefe@empresa.com" y copia oculta a "ana.correo@boterosoto.com.co"
    Y el correo tiene el asunto "Cotización PCN-12102026 — Botero Soto" y adjunta "PCN-12102026.pdf"

  Escenario: El worker confirma la entrega de MailSender
    Dado que "ana.correo" tiene la cotización "PCN-13102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-13102026" a "cliente@empresa.com"
    Cuando MailSender entrega el último correo
    Y pasa 1 minuto y Cotifly consulta los correos de "PCN-13102026"
    Entonces el último correo de "PCN-13102026" queda registrado como "enviado"

  Escenario: Un correo que MailSender no logra entregar queda fallido y el comercial lo reintenta
    Dado que "ana.correo" tiene la cotización "PCN-14102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-14102026" a "cliente@empresa.com"
    Cuando MailSender no logra entregar el último correo por "550 5.7.1 Unable to relay"
    Y pasa 1 minuto y Cotifly consulta los correos de "PCN-14102026"
    Entonces el último correo de "PCN-14102026" queda registrado como "fallido" con el error "550 5.7.1 Unable to relay"
    Y la cotización "PCN-14102026" queda en estado de envío "enviada"
    Cuando "luis.correo" reintenta el último correo de "PCN-14102026"
    Entonces el reintento se rechaza con el código "COTIZACION_NO_ENCONTRADA"
    Cuando "ana.correo" reintenta el último correo de "PCN-14102026"
    Entonces el reintento es exitoso
    Y el último correo de "PCN-14102026" queda registrado como "encolado"
    Cuando MailSender entrega el último correo
    Y pasa 1 minuto y Cotifly consulta los correos de "PCN-14102026"
    Entonces el último correo de "PCN-14102026" queda registrado como "enviado"

  Escenario: Un segundo envío exitoso deja la cotización como reenviada
    Dado que "ana.correo" tiene la cotización "PCN-02102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-02102026" a "cliente@empresa.com"
    Cuando pasan 6 minutos
    Y "ana.correo" envía la cotización "PCN-02102026" a "otro@empresa.com"
    Entonces el envío es exitoso
    Y la cotización "PCN-02102026" queda en estado de envío "reenviada"

  Escenario: Un envío idéntico en menos de 5 minutos se rechaza como duplicado aunque siga encolado
    Dado que "ana.correo" tiene la cotización "PCN-03102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-03102026" a "cliente@empresa.com"
    Cuando pasan 4 minutos
    Y "ana.correo" envía la cotización "PCN-03102026" a "CLIENTE@empresa.com"
    Entonces el envío se rechaza con el código "CORREO_DUPLICADO"

  Escenario: Pasados 5 minutos el mismo envío se permite
    Dado que "ana.correo" tiene la cotización "PCN-04102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-04102026" a "cliente@empresa.com"
    Cuando pasan 6 minutos
    Y "ana.correo" envía la cotización "PCN-04102026" a "cliente@empresa.com"
    Entonces el envío es exitoso

  Escenario: Un comercial no puede enviar la cotización de otro
    Dado que "ana.correo" tiene la cotización "PCN-05102026" del cliente "900123456" con su PDF
    Cuando "luis.correo" envía la cotización "PCN-05102026" a "cliente@empresa.com"
    Entonces el envío se rechaza con el código "COTIZACION_NO_ENCONTRADA"
    Y MailSender no recibe ningún correo

  Escenario: Un rechazo de MailSender queda registrado como fallido y no cambia el estado
    Dado que "ana.correo" tiene la cotización "PCN-06102026" del cliente "900123456" con su PDF
    Y MailSender rechaza los correos con el estado 422
    Cuando "ana.correo" envía la cotización "PCN-06102026" a "cliente@empresa.com"
    Entonces el envío se rechaza con el código "CORREO_ENVIO_FALLIDO"
    Y el último correo de "PCN-06102026" queda registrado como "fallido"
    Y la cotización "PCN-06102026" queda en estado de envío "sin_enviar"

  Escenario: Sin PDF en el almacén no se envía
    Dado que "ana.correo" tiene la cotización "PCN-07102026" del cliente "900123456" sin PDF
    Cuando "ana.correo" envía la cotización "PCN-07102026" a "cliente@empresa.com"
    Entonces el envío se rechaza con el código "CORREO_PDF_NO_ENCONTRADO"

  Escenario: Con el correo deshabilitado no se envía nada
    Dado que "ana.correo" tiene la cotización "PCN-08102026" del cliente "900123456" con su PDF
    Y el envío de correo deshabilitado
    Cuando "ana.correo" envía la cotización "PCN-08102026" a "cliente@empresa.com"
    Entonces el envío se rechaza con el código "CORREO_DESHABILITADO"
    Y MailSender no recibe ningún correo

  Escenario: Los destinatarios del último envío al cliente se precargan
    Dado que "ana.correo" tiene la cotización "PCN-09102026" del cliente "800765432" con su PDF
    Y que "ana.correo" envió la cotización "PCN-09102026" a "compras@cliente.com" con copia a "gerente@cliente.com"
    Cuando "ana.correo" consulta los destinatarios previos del cliente "800765432"
    Entonces los destinatarios previos son "compras@cliente.com" con copia a "gerente@cliente.com"
    Cuando "luis.correo" consulta los destinatarios previos del cliente "800765432"
    Entonces no hay destinatarios previos

  Escenario: La vista previa muestra el correo sin enviarlo, precarga los destinatarios y avisa que es un ambiente de pruebas
    Dado que "ana.correo" tiene la cotización "PCN-10102026" del cliente "700111222" con su PDF
    Y que "ana.correo" tiene la cotización "PCN-11102026" del cliente "700111222" con su PDF
    Y que "ana.correo" envió la cotización "PCN-10102026" a "compras@cliente.com"
    Cuando "ana.correo" pide la vista previa del correo de "PCN-11102026"
    Entonces la vista previa tiene el asunto "Cotización PCN-11102026 — Botero Soto" y adjunta "PCN-11102026.pdf"
    Y la vista previa trae precargado "compras@cliente.com"
    Y la vista previa avisa que es un ambiente de pruebas
    Y la vista previa no envía ningún correo
