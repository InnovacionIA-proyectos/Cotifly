using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Licitaciones.Validators;

/// <summary>
/// Validación completa de una licitación ANTES de guardar o enviar nada (HU-011.2, <c>docs/conexion_nexo/HU-011-formato-excel-licitacion.md</c> §5):
/// espejo de <c>CapturarSolicitudValidador</c> y de los límites de <c>CapturaSolicitudRequest</c> de Nexo, más las reglas de Cotifly
/// (cliente, línea de negocio, tope de líneas, filas del Excel). A diferencia de Nexo, no se detiene en el primer error: devuelve
/// todos, con el índice de la línea, la fila del análisis, el campo y el código.
/// </summary>
public static class clsValidadorLicitacion
{
    /// <summary>Tope de líneas de una licitación: las mismas 1000 filas por hoja que devuelve el análisis de Nexo.</summary>
    public const int MaximoLineas = 1000;

    /// <summary>
    /// Errores de la licitación (vacía si es válida). <paramref name="filasEnHoja"/> es el número de filas con datos de la hoja del
    /// archivo adjunto (<c>null</c> sin archivo): con archivo, cada línea debe traer una <c>fila</c> menor que ese número.
    /// <paramref name="filasExcel"/> (HU-013.15, #124), si se conoce, es el número de fila de Excel de cada fila con datos: los mensajes
    /// nombran las filas con esa numeración (la que ve el comercial) y, sin ella, por la posición de la línea (base 1). El índice
    /// <c>fila</c> (base 0) nunca aparece en un mensaje.
    /// </summary>
    public static IReadOnlyList<clsErrorFilaLicitacion> Validar(clsEntradaLicitacion entrada, int? filasEnHoja, IReadOnlyList<int>? filasExcel = null)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var errores = new List<clsErrorFilaLicitacion>();
        void Encabezado(string campo, string codigo, string mensaje) => errores.Add(new clsErrorFilaLicitacion(null, null, campo, codigo, mensaje));

        var nit = entrada.NitCliente?.Trim();
        if (string.IsNullOrEmpty(nit))
        {
            Encabezado("nit_cliente", clsCodigosLineaSolicitud.CampoRequerido, "El campo 'nit_cliente' es obligatorio: elige el cliente en el buscador.");
        }
        else if (nit.Length > clsEntradaSolicitudCosteo.LargoMaximoNit)
        {
            Encabezado("nit_cliente", clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo 'nit_cliente' supera {clsEntradaSolicitudCosteo.LargoMaximoNit} caracteres.");
        }

        if (entrada.ClienteNombre is { Length: > clsEntradaSolicitudCosteo.LargoMaximoNombre })
        {
            Encabezado("cliente_nombre", clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo 'cliente_nombre' supera {clsEntradaSolicitudCosteo.LargoMaximoNombre} caracteres.");
        }

        if (string.IsNullOrWhiteSpace(entrada.LineaNegocio))
        {
            Encabezado("linea_negocio", clsCodigosLineaSolicitud.CampoRequerido, "El campo 'linea_negocio' es obligatorio.");
        }

        if (entrada.Moneda is { } moneda && (string.IsNullOrWhiteSpace(moneda) || clsConversorMoneda.Normalizar(moneda).EsFallo))
        {
            Encabezado("moneda", clsCodigosLineaSolicitud.ValorFueraDeRango, $"El campo 'moneda' debe ser COP o USD (llegó '{moneda.Trim()}').");
        }

        foreach (var error in entrada.Encabezado.Errores())
        {
            Encabezado(error.Campo, error.Codigo, error.Mensaje);
        }

        var conArchivo = entrada.Archivo is not null;
        if (conArchivo && string.IsNullOrWhiteSpace(entrada.Hoja))
        {
            Encabezado("hoja", clsCodigosLineaSolicitud.CampoRequerido, "El campo 'hoja' es obligatorio cuando se adjunta el archivo.");
        }
        else if (entrada.Hoja is { Length: > clsArchivoLicitacion.LargoHoja })
        {
            Encabezado("hoja", clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo 'hoja' supera {clsArchivoLicitacion.LargoHoja} caracteres.");
        }

        var lineas = entrada.Lineas ?? [];
        if (lineas.Count == 0)
        {
            Encabezado("lineas", clsCodigosLineaSolicitud.LineasRequeridas, "La licitación debe traer al menos una línea.");
            return errores;
        }

        if (lineas.Count > MaximoLineas)
        {
            Encabezado("lineas", clsCodigosLineaSolicitud.DemasiadasLineas, $"Una licitación admite máximo {MaximoLineas} líneas (llegaron {lineas.Count}).");
            return errores;
        }

        var vistas = new HashSet<int>();
        for (var i = 0; i < lineas.Count; i++)
        {
            var entradaLinea = lineas[i];
            var fila = entradaLinea?.Fila;
            void Linea(string campo, string codigo, string mensaje) => errores.Add(new clsErrorFilaLicitacion(i, fila, campo, codigo, mensaje));

            if (entradaLinea?.Linea is null)
            {
                Linea("lineas", clsCodigosLineaSolicitud.LineaIncompleta, $"La línea {i + 1} está vacía.");
                continue;
            }

            // #278: la pareja de horas de la fila se valida completada con la del encabezado.
            foreach (var error in entradaLinea.Linea.Errores().Concat(entradaLinea.Linea.ErroresHorasEnPareja(entrada.Encabezado)))
            {
                Linea(error.Campo, error.Codigo, error.Mensaje);
            }

            // HU-013.15 (#124): los mensajes nombran la línea (base 1) y la fila de Excel, nunca el índice 'fila' (base 0).
            if (fila is null)
            {
                if (conArchivo)
                {
                    Linea("fila", clsCodigosLineaSolicitud.FilaInvalida,
                        $"La línea {i + 1} no trae su 'fila' del análisis: con el archivo adjunto cada línea debe traerla.");
                }
            }
            else if (fila < 0)
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, $"La línea {i + 1} trae una 'fila' negativa.");
            }
            else if (!vistas.Add(fila.Value))
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, NumeroFilaExcel(filasExcel, fila.Value) is { } repetida
                    ? $"La línea {i + 1} repite la fila {repetida} del Excel, que ya está en otra línea."
                    : $"La línea {i + 1} repite la 'fila' de otra línea.");
            }
            else if (filasEnHoja is { } total && fila >= total)
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida,
                    $"La línea {i + 1} apunta a una fila que no está en la hoja '{entrada.Hoja}': la hoja tiene {total} filas con datos"
                    + (filasExcel is { Count: > 0 } ? $" (de la fila {filasExcel[0]} a la {filasExcel[^1]} del Excel)." : "."));
            }
        }

        return errores;
    }

    /// <summary>
    /// Número de fila de Excel (base 1, encabezado incluido) del índice <paramref name="fila"/> de <c>hojas[h].filas</c>, o <c>null</c>
    /// si no se conoce o no existe en la hoja.
    /// </summary>
    public static int? NumeroFilaExcel(IReadOnlyList<int>? filasExcel, int? fila) =>
        filasExcel is not null && fila is { } f && f >= 0 && f < filasExcel.Count ? filasExcel[f] : null;

    /// <summary>
    /// Los errores con su <c>fila_excel</c> (HU-013.15, #124): el número de fila de Excel de la <c>fila</c> de cada uno, si se conoce.
    /// </summary>
    public static IReadOnlyList<clsErrorFilaLicitacion> ConFilaExcel(IReadOnlyList<clsErrorFilaLicitacion> errores, IReadOnlyList<int>? filasExcel)
    {
        ArgumentNullException.ThrowIfNull(errores);
        return filasExcel is null ? errores : [.. errores.Select(e => e with { FilaExcel = NumeroFilaExcel(filasExcel, e.Fila) })];
    }
}
