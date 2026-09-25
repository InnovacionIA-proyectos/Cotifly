# language: es
# HU-003.5 (#11) — Envío de la cotización por correo (port de mcp_server/correo.py, transporte smtp_relay).
# Se ejecuta en Cotifly.Aceptacion.Tests con el registro real y un servidor SMTP falso en proceso
# (127.0.0.1, sin red) en el papel del relay interno. Cada escenario usa un folio distinto.
# El transporte real de producción está por confirmar con el equipo (D3); smtp y wscorreos no se portan.
@integracion
Característica: Envío de la cotización por correo
  Como comercial de Botero Soto
  Quiero enviar la cotización al cliente con el PDF adjunto desde mi correo
  Para que el cliente la reciba y la trazabilidad quede en la base

  Antecedentes:
    Dado el comercial "ana.correo" con nombre "Ana Correo" y correo "ana.correo@boterosoto.com.co"
    Y el comercial "luis.correo" con nombre "Luis Correo" y correo "luis.correo@boterosoto.com.co"
    Y el envío de correo habilitado con el relay SMTP de pruebas

  Escenario: El envío adjunta el PDF, sale con el correo del comercial y marca la cotización como enviada
    Dado que "ana.correo" tiene la cotización "PCN-01102026" del cliente "900123456" con su PDF
    Cuando "ana.correo" envía la cotización "PCN-01102026" a "cliente@empresa.com" con copia a "jefe@empresa.com"
    Entonces el envío es exitoso
    Y el relay recibe el correo de "ana.correo@boterosoto.com.co" para "cliente@empresa.com" y "jefe@empresa.com"
    Y el correo tiene el asunto "Cotización PCN-01102026 — Botero Soto" y adjunta "PCN-01102026.pdf"
    Y el último correo de "PCN-01102026" queda registrado como "enviado"
    Y la cotización "PCN-01102026" queda en estado de envío "enviada"

  Escenario: Un segundo envío exitoso deja la cotización como reenviada
    Dado que "ana.correo" tiene la cotización "PCN-02102026" del cliente "900123456" con su PDF
    Y que "ana.correo" envió la cotización "PCN-02102026" a "cliente@empresa.com"
    Cuando pasan 6 minutos
    Y "ana.correo" envía la cotización "PCN-02102026" a "otro@empresa.com"
    Entonces el envío es exitoso
    Y la cotización "PCN-02102026" queda en estado de envío "reenviada"

  Escenario: Un envío idéntico en menos de 5 minutos se rechaza como duplicado
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
    Y el relay no recibe ningún correo

  Escenario: Un fallo del relay queda registrado como fallido y no cambia el estado
    Dado que "ana.correo" tiene la cotización "PCN-06102026" del cliente "900123456" con su PDF
    Y el relay rechaza los destinatarios
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
    Y el relay no recibe ningún correo

  Escenario: Los destinatarios del último envío al cliente se precargan
    Dado que "ana.correo" tiene la cotización "PCN-09102026" del cliente "800765432" con su PDF
    Y que "ana.correo" envió la cotización "PCN-09102026" a "compras@cliente.com" con copia a "gerente@cliente.com"
    Cuando "ana.correo" consulta los destinatarios previos del cliente "800765432"
    Entonces los destinatarios previos son "compras@cliente.com" con copia a "gerente@cliente.com"
    Cuando "luis.correo" consulta los destinatarios previos del cliente "800765432"
    Entonces no hay destinatarios previos

  Escenario: La vista previa muestra el correo sin enviarlo y precarga los destinatarios previos del cliente
    Dado que "ana.correo" tiene la cotización "PCN-10102026" del cliente "700111222" con su PDF
    Y que "ana.correo" tiene la cotización "PCN-11102026" del cliente "700111222" con su PDF
    Y que "ana.correo" envió la cotización "PCN-10102026" a "compras@cliente.com"
    Cuando "ana.correo" pide la vista previa del correo de "PCN-11102026"
    Entonces la vista previa tiene el asunto "Cotización PCN-11102026 — Botero Soto" y adjunta "PCN-11102026.pdf"
    Y la vista previa trae precargado "compras@cliente.com"
    Y la vista previa no envía ningún correo
