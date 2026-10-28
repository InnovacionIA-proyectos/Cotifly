# language: es
# HU-034.1 (#201, parte A) — El gerente comercial (solo su regional) y el director (cualquier regional, HU-037.3) ven la licitación
# (GET /web/licitaciones/{folio}) y bajan su Excel (GET /web/licitaciones/{folio}/excel) en solo consulta y sin costos, con la misma
# regla de alcance que el detalle de la cotización y el PDF (IClsRegionalUsuario.PuedeVerDeAsync, fail-closed con 404) y con auditoría.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y un Nexo falso (nunca el real).
@integracion
Característica: Licitación de un comercial vista por su gerente y por el director
  Como gerente comercial de una regional (o director)
  Quiero ver la licitación de un comercial de mi regional y bajar su Excel
  Para hacer seguimiento a sus licitaciones sin poder cambiarlas ni ver su costo

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y el gerente comercial "hugo" con sesión en la regional "Norte"
    Y "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"

  Escenario: El dueño ve su licitación y no es de solo consulta
    Cuando "ana" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "Licitacion" del contrato
    Y la licitación consultada es "L1" y no es de solo consulta

  Escenario: El gerente de la regional del dueño ve la licitación en solo consulta y sin costos
    Cuando "gloria" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "Licitacion" del contrato
    Y la licitación consultada es "L1" y es de solo consulta
    Y la licitación consultada no contiene el costo 1000000 de Nexo
    Y la consulta de "gloria" a la licitación "L1" queda en la auditoría como permitida

  Escenario: El director ve en solo consulta la licitación de otra regional
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "Licitacion" del contrato
    Y la licitación consultada es "L1" y es de solo consulta

  Escenario: Un gerente de otra regional, un gerente sin regional y un comercial ajeno no la ven
    Dado el gerente comercial "irene" con sesión y sin regional
    Cuando "hugo" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Y la consulta de "hugo" a la licitación "L1" queda en la auditoría como negada
    Cuando "irene" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Y la consulta de "irene" a la licitación "L1" queda en la auditoría como negada
    Cuando "luis" consulta la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."

  Escenario: El gerente de la regional y el director bajan el mismo Excel del dueño, sin costos
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "ana" exporta el Excel de la licitación "L1"
    Y "gloria" exporta el Excel de la licitación "L1"
    Entonces el Excel es igual al que bajó "ana"
    Y el Excel no contiene el costo 1000000 de Nexo
    Y la descarga del Excel de "gloria" de la licitación "L1" queda en la auditoría como permitida
    Cuando "dario" exporta el Excel de la licitación "L1"
    Entonces el Excel es igual al que bajó "ana"

  Escenario: Un gerente de otra regional y un comercial ajeno no bajan el Excel
    Cuando "hugo" intenta exportar el Excel de la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Cuando "luis" intenta exportar el Excel de la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."

  Escenario: El gerente y el director no pueden cambiar la licitación
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "gloria" intenta reintentar la cubicación de la licitación "L1" con el respaldo
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Cuando "dario" intenta reintentar la cubicación de la licitación "L1" sin el respaldo
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
    Cuando "gloria" intenta reintentar el envío de la licitación "L1"
    Entonces la respuesta tiene el estado 404 con el error "Cotización no encontrada o no es tuya."
