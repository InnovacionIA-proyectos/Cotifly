# language: es
# HU-003.5 (#11) — PDF de la cotización (ADR-006: PDFsharp-MigraDoc con Liberation Sans embebida).
# Port de mcp_server/pdf/scripts/armar_cotizacion.py (tarifa única) y armar_multi.py (secciones).
# Se ejecuta en Cotifly.Aceptacion.Tests con el registro real (AddAplicacion + AddInfraestructura),
# SQL Server 2019 en Testcontainers y el almacén de PDFs en una carpeta temporal. El texto del PDF
# se extrae con PdfPig (solo en pruebas). Cada escenario usa un folio distinto (base compartida).
# El folio llega en la entrada: generarlo es de HU-003.6 (#12, ADR-011).
@integracion
Característica: PDF de la cotización
  Como comercial de Botero Soto
  Quiero generar el PDF corporativo de la cotización con mis datos reales
  Para enviarlo al cliente con el mismo formato que hoy genera el backend Python

  Antecedentes:
    Dado el comercial "nathaly.vargas" con nombre "Nathaly Vargas Garcia" y correo "nvargas@boterosoto.com.co"

  Escenario: El PDF de tarifa única tiene tres páginas y queda registrado
    Dado que "nathaly.vargas" tiene el teléfono "313 415 6187"
    Cuando "nathaly.vargas" genera el PDF de la solicitud de ejemplo con el folio "PCN-19032026"
    Entonces la cotización "PCN-19032026" queda registrada de "nathaly.vargas" con la línea "Nacional" y estado de envío "sin_enviar"
    Y el PDF tiene 3 páginas
    Y el PDF contiene "LONGYIN"
    Y el PDF contiene "$3.000.000"
    Y el PDF contiene "$3.600.000"
    Y el PDF contiene "Yumbo, 19 de marzo de 2026"
    Y el PDF contiene "NUESTRA TARIFA PARA SU SOLICITUD ES:"
    Y el PDF contiene "Nathaly Vargas Garcia"
    Y el PDF contiene "Cel 313 415 6187"
    Y el PDF queda en el almacén como "PCN-19032026.pdf"

  Escenario: Sin teléfono registrado el PDF omite el celular aunque el payload traiga uno
    Cuando "nathaly.vargas" genera el PDF de la solicitud de ejemplo con el folio "PCN-19032026-v2"
    Entonces el PDF no contiene "Cel"
    Y el PDF no contiene "3134156187"

  Escenario: El bloque aduanero solo aparece en la línea Internacional
    Cuando "nathaly.vargas" genera el PDF de la línea "Internacional" con aduana en frontera "Ipiales" y el folio "PCN-21032026"
    Entonces el PDF contiene "Datos aduaneros (ANDINO)"
    Y el PDF contiene "Ipiales"
    Cuando "nathaly.vargas" genera el PDF de la línea "Nacional" con aduana en frontera "Ipiales" y el folio "PCN-22032026"
    Entonces el PDF no contiene "Datos aduaneros"

  Escenario: Una cotización con secciones usa la variante multi-sección
    Cuando "nathaly.vargas" genera el PDF con las secciones "TRAMO 1" y "TRAMO 2" y el folio "PCN-23032026"
    Entonces el PDF tiene 2 páginas
    Y el PDF contiene "TRAMO 1"
    Y el PDF contiene "TRAMO 2"
    Y el PDF contiene "DESTINO / SITIO"

  Escenario: Los servicios conexos se muestran itemizados con su precio
    Cuando "nathaly.vargas" genera el PDF con el servicio conexo "Levante de contenedor" por 350000 y el folio "PCN-24032026"
    Entonces el PDF contiene "Levante de contenedor: $350.000"
    Y el PDF contiene "Flete YUMBO – BUN"

  Escenario: Un folio que ya existe se rechaza sin reemplazar el PDF anterior
    Dado que "nathaly.vargas" generó el PDF de la solicitud de ejemplo con el folio "PCN-25032026"
    Cuando "nathaly.vargas" genera el PDF de la solicitud de ejemplo con el folio "PCN-25032026"
    Entonces la generación se rechaza con el código "FOLIO_DUPLICADO"
    Y el PDF "PCN-25032026.pdf" del almacén es el de la primera generación

  Escenario: Una línea de negocio desconocida se rechaza antes de renderizar
    Cuando "nathaly.vargas" genera el PDF de la línea "Marítimo" con el folio "PCN-26032026"
    Entonces la generación se rechaza con el código "MARGEN_LINEA_DESCONOCIDA"
    Y no hay PDF "PCN-26032026.pdf" en el almacén

  Escenario: La línea con tilde se resuelve contra la política sembrada (issue #44)
    Cuando "nathaly.vargas" genera el PDF de la línea "Distribución" con el folio "PCN-27032026"
    Entonces la cotización "PCN-27032026" queda registrada de "nathaly.vargas" con la línea "Distribucion" y estado de envío "sin_enviar"

  Escenario: El texto del payload se muestra literal, sin interpretarse como HTML
    Cuando "nathaly.vargas" genera el PDF para el cliente "<script>alert(1)</script>" con el folio "PCN-28032026"
    Entonces el PDF contiene "<script>alert(1)</script>"

  Escenario: El teléfono confirmado por el comercial queda guardado y se imprime
    Cuando "nathaly.vargas" confirma el teléfono "+57 300 123 4567"
    Y "nathaly.vargas" genera el PDF de la solicitud de ejemplo con el folio "PCN-29032026"
    Entonces el teléfono de "nathaly.vargas" es "+57 300 123 4567"
    Y el PDF contiene "Cel +57 300 123 4567"
