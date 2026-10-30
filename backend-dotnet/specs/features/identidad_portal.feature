# language: es
# #172 (identidad compartida con Nexo) — Cotifly y Nexo resuelven la identidad del portal contra la misma base (en QA,
# dbSolucionesTI_Prueba). ClientePro manda el NOMBRE de usuario; Cotifly lo busca por NOMBREUSUARIO (sin distinguir mayúsculas), guarda
# el ID de esa fila en UsuariosWeb.idUsuarioExterno y ese mismo número viaja a Nexo en X-Portal-clsUsuario-Id en toda petición.
# Al cambiar de base de identidad, el DBA suelta el vínculo (idUsuarioExterno = NULL, script operacion/revincular_…sql) y el siguiente
# login o refresh lo vuelve a llenar con el ID nuevo, sobre la misma cuenta.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso (nunca los reales). Los IDs del portal son
# únicos por escenario. Pasos en Cotifly.Aceptacion.Tests/Identidad/clsPasosIdentidadPortal.cs.
@integracion
Característica: Identidad del portal compartida con Nexo
  Como comercial que entra a Cotifly desde ClientePro
  Quiero que Nexo me reconozca con el mismo ID del portal que usa Cotifly
  Para que mis solicitudes de costeo queden a mi nombre en Nexo

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"

  Escenario: El login con el nombre de usuario guarda el ID del portal y ese ID viaja a Nexo
    Dado que en el portal "dguerra" está registrado en mayúsculas con un ID propio y el rol externo de comercial
    Cuando ClientePro inicia la sesión de "dguerra" con su nombre de usuario en minúsculas
    Entonces la sesión de "dguerra" se emite con el rol "comercial"
    Y Cotifly guarda el ID del portal de "dguerra" como su identidad externa
    Cuando "dguerra" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Entonces la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y toda petición que recibió Nexo lleva el ID del portal de "dguerra" en X-Portal-clsUsuario-Id

  Escenario: Con el vínculo en NULL el siguiente login vincula la misma cuenta al ID nuevo del portal
    Dado que en el portal "rvinculo" está registrado en mayúsculas con un ID propio y el rol externo de comercial
    Y ClientePro inicia la sesión de "rvinculo" con su nombre de usuario en minúsculas
    Cuando la base de identidad cambia y "rvinculo" tiene otro ID en el portal
    Entonces el login de "rvinculo" responde credenciales inválidas
    Cuando el DBA suelta el vínculo de identidad de "rvinculo"
    Y ClientePro inicia la sesión de "rvinculo" con su nombre de usuario en minúsculas
    Entonces la sesión de "rvinculo" es la misma cuenta de Cotifly
    Y Cotifly guarda el ID del portal de "rvinculo" como su identidad externa

  Escenario: Con el vínculo en NULL el refresh también vincula al ID nuevo del portal
    Dado que en el portal "rrefresh" está registrado en mayúsculas con un ID propio y el rol externo de comercial
    Y ClientePro inicia la sesión de "rrefresh" con su nombre de usuario en minúsculas
    Cuando la base de identidad cambia y "rrefresh" tiene otro ID en el portal
    Y el DBA suelta el vínculo de identidad de "rrefresh"
    Y "rrefresh" renueva su sesión con el refresh vigente
    Entonces el refresh de "rrefresh" responde 200
    Y Cotifly guarda el ID del portal de "rrefresh" como su identidad externa
