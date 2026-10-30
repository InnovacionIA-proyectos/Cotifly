# language: es
# HU-003.6 (#12, parte A) — Login SSO desde ClientePro con verificación DOBLE (ADR-009).
# ClientePro ya autenticó a la persona; Cotifly revalida en cada login:
#   1. Externo (dbsolucionesti, solo lectura): la persona existe en tblSAPUsuariosApp (idUsuario como
#      NOMBREUSUARIO y, si no, como ID) y sus roles de tblSAPUsuarioRoles, traducidos con Roles.idRolExterno,
#      dan un rol interno (comercial | gerente_comercial | director; HU-037, #210).
#   2. Interno (dbo_cotifly): en el primer ingreso se crea el usuario (JIT) y se le asigna ese rol en
#      UsuariosRoles; en los siguientes, el rol externo actual debe coincidir con el asignado y el NOMBREUSUARIO
#      con el del usuario vinculado. Si algo difiere: 401 genérico y auditoría, hasta que se revise.
# Los roles del body nunca deciden. Cualquier falla responde el mismo 401 "Credenciales inválidas".
# Roles externos de las pruebas (los mismos IDROLES de dbSolucionesTI_Prueba, ADR-009): 32 = ClienteProComercial, 31 = ClienteProGerencial, 40 = sin mapeo.
# HU-037 (#210): 33 = director en estas pruebas (en la compartida el director es el 31 y el gerente el 25, provisional, #209).
@integracion
Característica: Login SSO desde ClientePro con verificación doble
  Como comercial de Botero Soto que ya inició sesión en ClientePro
  Quiero entrar a Cotifly sin credenciales propias
  Para cotizar con mi identidad y mi rol reales, sin que nadie pueda autoasignarse un rol

  Escenario: El primer ingreso crea el usuario y le asigna el rol según su rol externo
    Dado la persona "ana.sso" con ID externo 5101 y nombre "Ana SSO" en el almacén corporativo con los roles externos "32"
    Cuando "ana.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "comercial"
    Y el usuario "ana.sso" existe en Cotifly con nombre "Ana SSO" y vinculado al ID externo 5101
    Y el usuario "ana.sso" tiene asignado el rol "comercial"
    Y el acceso de "ana.sso" queda auditado como exitoso

  Escenario: El usuario se identifica también por su ID numérico del almacén
    Dado la persona "beto.sso" con ID externo 5102 y nombre "Beto SSO" en el almacén corporativo con los roles externos "32"
    Cuando la persona con idUsuario "5102" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "comercial"
    Y el usuario "beto.sso" existe en Cotifly con nombre "Beto SSO" y vinculado al ID externo 5102

  Escenario: Un segundo ingreso con el mismo rol externo entra sin crear otro usuario
    Dado la persona "caro.sso" con ID externo 5103 y nombre "Caro SSO" en el almacén corporativo con los roles externos "31"
    Y "caro.sso" ya inició sesión una vez
    Cuando "caro.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "gerente_comercial"
    Y existe un solo usuario "caro.sso" en Cotifly

  Escenario: Si el rol externo cambió después del primer ingreso, se rechaza hasta que se revise
    Dado la persona "dani.sso" con ID externo 5104 y nombre "Dani SSO" en el almacén corporativo con los roles externos "32"
    Y "dani.sso" ya inició sesión una vez
    Y en el almacén corporativo "dani.sso" pasa a tener los roles externos "31"
    Cuando "dani.sso" inicia sesión desde ClientePro
    Entonces el login responde credenciales inválidas
    Y el usuario "dani.sso" sigue con el rol "comercial"
    Y el acceso de "dani.sso" queda auditado como rechazado por "rol_externo_cambio"

  Escenario: Si el NOMBREUSUARIO cambió en el almacén, no se crea otro usuario y se rechaza
    Dado la persona "eli.sso" con ID externo 5105 y nombre "Eli SSO" en el almacén corporativo con los roles externos "32"
    Y "eli.sso" ya inició sesión una vez
    Y en el almacén corporativo el ID externo 5105 pasa a llamarse "eli.sso2"
    Cuando "eli.sso2" inicia sesión desde ClientePro
    Entonces el login responde credenciales inválidas
    Y no existe el usuario "eli.sso2" en Cotifly
    Y el acceso de "eli.sso2" queda auditado como rechazado por "nombre_usuario_cambio"

  Escenario: Sin un rol externo de Comercial o Gerente Comercial no se entra
    Dado la persona "fer.sso" con ID externo 5106 y nombre "Fer SSO" en el almacén corporativo con los roles externos "40"
    Cuando "fer.sso" inicia sesión desde ClientePro
    Entonces el login responde credenciales inválidas
    Y no existe el usuario "fer.sso" en Cotifly
    Y el acceso de "fer.sso" queda auditado como rechazado por "sin_rol_autorizado"

  Escenario: Los roles que envía ClientePro en el body no deciden el rol
    Dado la persona "gabi.sso" con ID externo 5107 y nombre "Gabi SSO" en el almacén corporativo con los roles externos "32"
    Cuando "gabi.sso" inicia sesión desde ClientePro declarando los roles "31"
    Entonces la sesión se emite con el rol "comercial"

  # HU-040.2 (#230, ADR-025): con varios roles ya no gana el de mayor alcance: el login no emite sesión y la persona elige.
  Escenario: Con los roles externos de comercial y gerente el primer ingreso pide elegir y entra con el rol elegido
    Dado la persona "hugo.sso" con ID externo 5108 y nombre "Hugo SSO" en el almacén corporativo con los roles externos "32,31"
    Cuando "hugo.sso" inicia sesión desde ClientePro
    Entonces el login pide elegir entre los roles "comercial,gerente_comercial"
    Y el usuario "hugo.sso" tiene asignados los roles "comercial,gerente_comercial"
    Y el acceso de "hugo.sso" queda auditado con la regla "multi_rol"
    Cuando "hugo.sso" elige el rol "comercial"
    Entonces la sesión se emite con el rol "comercial"
    Y la elección de "hugo.sso" queda auditada como aceptada

  Escenario: Con los roles externos de comercial y director elige director
    Dado la persona "dora.sso" con ID externo 5120 y nombre "Dora SSO" en el almacén corporativo con los roles externos "32,33"
    Cuando "dora.sso" inicia sesión desde ClientePro
    Entonces el login pide elegir entre los roles "comercial,director"
    Cuando "dora.sso" elige el rol "director"
    Entonces la sesión se emite con el rol "director"

  # HU-040.2: rol único. Quien ya tiene una sola asignación sigue con la jerarquía (director > gerente_comercial > comercial).
  Escenario: Quien ya tenía una sola asignación entra como siempre aunque tenga varios roles externos
    Dado la persona "lola.sso" con ID externo 5122 y nombre "Lola SSO" en el almacén corporativo con los roles externos "31"
    Y "lola.sso" ya inició sesión una vez
    Y en el almacén corporativo "lola.sso" pasa a tener los roles externos "32,31"
    Cuando "lola.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "gerente_comercial"
    Y el acceso de "lola.sso" queda auditado con la regla "precedencia_gerente_comercial"

  Escenario: El ticket de elección es de un solo uso
    Dado la persona "memo.sso" con ID externo 5123 y nombre "Memo SSO" en el almacén corporativo con los roles externos "32,31"
    Y "memo.sso" inició sesión desde ClientePro
    Y "memo.sso" elige el rol "comercial"
    Cuando "memo.sso" vuelve a elegir el rol "gerente_comercial" con el mismo ticket
    Entonces la elección responde sesión expirada
    Y la elección de "memo.sso" queda auditada como rechazada por "ticket_reutilizado"

  Escenario: El ticket de elección vence a los cinco minutos
    Dado la persona "nico.sso" con ID externo 5124 y nombre "Nico SSO" en el almacén corporativo con los roles externos "32,31"
    Y "nico.sso" inició sesión desde ClientePro
    Y pasaron 6 minutos
    Cuando "nico.sso" elige el rol "comercial"
    Entonces la elección responde sesión expirada

  Escenario: El refresh conserva el rol elegido
    Dado la persona "olga.sso" con ID externo 5125 y nombre "Olga SSO" en el almacén corporativo con los roles externos "32,31"
    Y "olga.sso" inició sesión desde ClientePro
    Y "olga.sso" elige el rol "comercial"
    Cuando "olga.sso" renueva su sesión
    Entonces la renovación emite un par nuevo con el rol "comercial"

  Escenario: El primer ingreso de un director crea el usuario con el rol director
    Dado la persona "dino.sso" con ID externo 5121 y nombre "Dino SSO" en el almacén corporativo con los roles externos "33"
    Cuando "dino.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "director"
    Y el acceso de "dino.sso" queda auditado con la regla "mapeo_unico"

  Escenario: Una persona que no existe en el almacén no entra
    Cuando "nadie.sso" inicia sesión desde ClientePro
    Entonces el login responde credenciales inválidas
    Y el acceso de "nadie.sso" queda auditado como rechazado por "usuario_no_encontrado"

  Escenario: Con el almacén corporativo caído el login falla cerrado
    Dado la persona "ivan.sso" con ID externo 5109 y nombre "Ivan SSO" en el almacén corporativo con los roles externos "32"
    Y el almacén corporativo no está disponible
    Cuando "ivan.sso" inicia sesión desde ClientePro
    Entonces el login responde credenciales inválidas
    Y no existe el usuario "ivan.sso" en Cotifly
    Y el acceso de "ivan.sso" queda auditado como rechazado por "almacen_no_disponible"

  # #81 (ADR-016 §6): la regional sale de DWCubosEBSSAP.DimEjecutivo, cuyo USUARIO trae el dominio pegado
  # ("boterosoto\usuario"). Se llena en el primer ingreso y se refresca en cada login; quien no aparece conserva la
  # que tenga (NULL o la del DBA). DimEjecutivo nunca bloquea el login.
  Escenario: El login llena la regional desde DimEjecutivo y la refresca en el siguiente ingreso
    Dado la persona "juli.sso" con ID externo 5110 y nombre "Juli SSO" en el almacén corporativo con los roles externos "32"
    Y en DimEjecutivo "juli.sso" aparece como "BOTEROSOTO\JULI.SSO" en la regional "Occidente"
    Cuando "juli.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "comercial"
    Y la sesión trae la ubicación "Occidente"
    Y el usuario "juli.sso" tiene la regional "Occidente" en Cotifly
    Cuando en DimEjecutivo "juli.sso" pasa a la regional "Norte"
    Y "juli.sso" inicia sesión desde ClientePro
    Entonces la sesión trae la ubicación "Norte"
    Y el usuario "juli.sso" tiene la regional "Norte" en Cotifly

  Escenario: Quien no aparece en DimEjecutivo entra con la regional vacía
    Dado la persona "kari.sso" con ID externo 5111 y nombre "Kari SSO" en el almacén corporativo con los roles externos "32"
    Y DimEjecutivo está conectada pero "kari.sso" no aparece en ella
    Cuando "kari.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "comercial"
    Y la sesión trae la ubicación vacía
    Y el usuario "kari.sso" tiene la regional vacía en Cotifly

  Escenario: Con DimEjecutivo caída el login entra y conserva la regional que tenía
    Dado la persona "leo.sso" con ID externo 5112 y nombre "Leo SSO" en el almacén corporativo con los roles externos "31"
    Y "leo.sso" ya inició sesión una vez
    Y el DBA asignó a "leo.sso" la regional "Antioquia"
    Y DimEjecutivo no está disponible
    Cuando "leo.sso" inicia sesión desde ClientePro
    Entonces la sesión se emite con el rol "gerente_comercial"
    Y la sesión trae la ubicación "Antioquia"
    Y el acceso de "leo.sso" queda auditado como exitoso
