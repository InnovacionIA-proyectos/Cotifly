# language: es
# HU-003.6 (#12, parte C) — Webhook de Google Chat (POST /chat/events) y worker: JWT de Google verificado con los certificados
# públicos de chat@system.gserviceaccount.com (aquí, falsos), acuse inmediato, turno en segundo plano y respuesta publicada en el
# hilo con la API de Chat y la cuenta de servicio (aquí, un Google falso sin red). La identidad es el correo del evento en
# UsuariosWeb.email (ADR-011): sin cuenta no se cotiza, y la verificación doble del login (rol externo actual = asignación
# activa) también se exige en cada turno. SQL Server 2019 en Testcontainers y el modelo guionizado.
@integracion
Característica: Cotizar desde Google Chat
  Como comercial de Botero Soto
  Quiero cotizar desde un hilo de Google Chat
  Para recibir la previsualización y el PDF donde ya converso con mi equipo

  Antecedentes:
    Dado la API de Cotifly conectada a Google Chat con el agente guionizado
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Un remitente sin cuenta en Cotifly recibe el aviso y no cotiza
    Cuando Google Chat envía el mensaje "Cotiza Nacional YUMBO a BUN" de "desconocido@gmail.com" en el hilo "T1"
    Entonces el webhook acusa recibo
    Y en el hilo "T1" se publica el texto "Ingresa primero a Cotifly desde ClientePro."
    Y el modelo no recibió ninguna llamada

  Escenario: El comercial cotiza por Google Chat y recibe la previsualización con los botones de confirmación
    Dado que en el turno 1 el modelo pide el margen de "Nacional" y calcula el precio con costo 2850000 y margen 0.40
    Cuando Google Chat envía el mensaje "Cotiza Nacional YUMBO a BUN, costo 2.850.000" de "ana" en el hilo "T2"
    Entonces el webhook acusa recibo
    Y en el hilo "T2" se publica la previsualización con el precio "$3.352.941" y el botón "Confirmar y generar PDF"
    Y la sesión del hilo "T2" quedó ligada a "ana"

  Escenario: Un evento con un JWT inválido se rechaza y no se procesa
    Cuando Google Chat envía el mensaje "hola" de "ana" en el hilo "T3" con un JWT inválido
    Entonces el webhook responde 401
    Y no se publicó nada en Google Chat

  Escenario: Si el rol externo del comercial cambió en ClientePro, Google Chat no cotiza (verificación doble)
    Dado que el rol externo de "ana" en ClientePro cambió a gerente comercial
    Cuando Google Chat envía el mensaje "Cotiza Nacional YUMBO a BUN" de "ana" en el hilo "T4"
    Entonces el webhook acusa recibo
    Y en el hilo "T4" se publica el texto "Ingresa primero a Cotifly desde ClientePro."
    Y el modelo no recibió ninguna llamada
