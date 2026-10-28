# language: es
# HU-004.9 (#41, incorporada a #12 parte B) — Administración de las políticas de margen por línea de negocio.
# Solo el rol gerente_comercial (HU-010) y, desde HU-037 (#210), el director; no un "admin" genérico. No se crean ni se borran líneas.
# Por HTTP contra la API real en memoria. Para no alterar las 8 líneas sembradas (las usan otros escenarios), cada
# escenario edita una línea de prueba propia ("LineaAdmin" es un alias).
@integracion
Característica: Administración de líneas de negocio
  Como gerente comercial
  Quiero ajustar el margen, el método de cálculo y el estado de cada línea de negocio
  Para que las cotizaciones usen la política vigente, con registro de quién la cambió

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y la línea de negocio de prueba "LineaAdmin" con margen 0.15, método "margen" y activa

  Escenario: El gerente lista todas las líneas con su política
    Cuando "gloria" lista las líneas de negocio
    Entonces la respuesta tiene el estado 200
    Y el listado de líneas incluye las 8 líneas sembradas y "LineaAdmin"
    Y cada línea trae id, linea_negocio, margen, metodo_calculo e is_active

  Escenario: El gerente edita una línea y el cambio queda auditado
    Cuando "gloria" actualiza la línea "LineaAdmin" con margen 0.2, método "markup" y activa "false"
    Entonces la respuesta tiene el estado 200
    Y la línea "LineaAdmin" queda con margen 0.2, método "markup" e inactiva
    Y la auditoría registra que "gloria" cambió "LineaAdmin" de margen 0.15 a 0.2

  # HU-037 (#210): el director usa todo lo del gerente, también la consola del comercial.
  Escenario: El director administra las líneas como el gerente y también usa la consola del comercial
    Dado el director "dario" con sesión en la regional "Norte"
    Cuando "dario" lista las líneas de negocio
    Entonces la respuesta tiene el estado 200
    Y el listado de líneas incluye las 8 líneas sembradas y "LineaAdmin"
    Cuando "dario" actualiza la línea "LineaAdmin" con margen 0.18, método "margen" y activa "true"
    Entonces la respuesta tiene el estado 200
    Y la línea "LineaAdmin" queda con margen 0.18, método "margen" y activa
    Y la auditoría registra que "dario" cambió "LineaAdmin" de margen 0.15 a 0.18
    Cuando "dario" consulta sus cotizaciones por la API
    Entonces la respuesta tiene el estado 200

  Escenario: Un comercial no puede listar ni editar líneas
    Cuando "ana" lista las líneas de negocio
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."
    Cuando "ana" actualiza la línea "LineaAdmin" con margen 0.3, método "margen" y activa "true"
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."
    Y la línea "LineaAdmin" queda con margen 0.15, método "margen" y activa

  Esquema del escenario: Un cambio inválido se rechaza sin tocar la línea
    Cuando "gloria" actualiza la línea "LineaAdmin" con el cuerpo <cuerpo>
    Entonces la respuesta tiene el estado 400
    Y la línea "LineaAdmin" queda con margen 0.15, método "margen" y activa

    Ejemplos:
      | cuerpo                                                          |
      | {"margen": 1.5, "metodo_calculo": "margen", "is_active": true}  |
      | {"margen": -0.1, "metodo_calculo": "margen", "is_active": true} |
      | {"margen": 0.2, "metodo_calculo": "otro", "is_active": true}    |
      | {"margen": 0.2, "is_active": true}                              |
      | {"margen": "0.2", "metodo_calculo": "margen", "is_active": true} |

  Escenario: Editar una línea que no existe responde 404
    Cuando "gloria" actualiza la línea con id 999999 con margen 0.2, método "margen" y activa "true"
    Entonces la respuesta tiene el estado 404
