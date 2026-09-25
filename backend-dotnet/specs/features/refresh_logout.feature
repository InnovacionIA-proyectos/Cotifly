# language: es
# HU-003.6 (#12, parte A) — Refresh con rotación atómica y logout (ADR-009).
# El refresh es opaco (256 bits), se guarda solo su SHA-256 y rota en cada uso con un UPDATE condicional: de dos
# usos simultáneos solo uno gana. Reutilizar un refresh ya rotado se trata como robo: 401 y se revocan todos los
# refresh activos del usuario. Cada refresh vuelve a verificar la identidad (almacén y rol asignado). El logout
# siempre responde 204: revoca la familia del refresh y registra el jti del access para rechazarlo hasta su vencimiento.
@integracion
Característica: Refresh y logout de la sesión
  Como comercial con sesión en Cotifly
  Quiero renovar mi sesión sin volver a entrar y poder cerrarla
  Para trabajar sin interrupciones y que nadie reutilice mis tokens

  Antecedentes:
    Dado la persona "rafa.sso" con ID externo 5201 y nombre "Rafa SSO" en el almacén corporativo con los roles externos "34"
    Y "rafa.sso" inició sesión desde ClientePro

  Escenario: El refresh emite un par nuevo e invalida el anterior
    Cuando "rafa.sso" renueva su sesión
    Entonces la renovación emite un par nuevo con el rol "comercial"
    Y el refresh anterior de "rafa.sso" ya no está vigente

  Escenario: Reutilizar un refresh ya rotado responde 401 y revoca los refresh activos del usuario
    Dado "rafa.sso" renovó su sesión hace 1 minuto
    Cuando "rafa.sso" vuelve a usar el refresh ya rotado
    Entonces la renovación responde sesión expirada
    Y "rafa.sso" no tiene refresh vigentes

  Escenario: De dos renovaciones simultáneas con el mismo refresh solo una gana
    Cuando "rafa.sso" renueva su sesión dos veces al mismo tiempo con el mismo refresh
    Entonces exactamente una renovación emite un par nuevo

  Escenario: Si el rol externo cambió, el refresh se rechaza
    Dado en el almacén corporativo "rafa.sso" pasa a tener los roles externos "35"
    Cuando "rafa.sso" renueva su sesión
    Entonces la renovación responde sesión expirada

  Escenario: Un refresh vencido no renueva la sesión
    Dado pasaron 31 días
    Cuando "rafa.sso" renueva su sesión
    Entonces la renovación responde sesión expirada

  Escenario: El logout revoca el refresh y el access
    Cuando "rafa.sso" cierra su sesión
    Entonces el logout responde sin error
    Y el refresh de "rafa.sso" ya no está vigente
    Y el access de "rafa.sso" figura como revocado

  Escenario: El logout sin tokens también responde sin error
    Cuando alguien cierra sesión sin tokens
    Entonces el logout responde sin error
