using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.Validators;

/// <summary>
/// Validación completa de una licitación ANTES de guardar o enviar nada (HU-011.2, <c>docs/HU-011-formato-excel-licitacion.md</c> §5):
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
    /// </summary>
    public static IReadOnlyList<clsErrorFilaLicitacion> Validar(clsEntradaLicitacion entrada, int? filasEnHoja)
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

            foreach (var error in entradaLinea.Linea.Errores())
            {
                Linea(error.Campo, error.Codigo, error.Mensaje);
            }

            if (fila is null)
            {
                if (conArchivo)
                {
                    Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, "Con el archivo adjunto cada línea debe traer su 'fila' del análisis.");
                }
            }
            else if (fila < 0)
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, "La 'fila' no puede ser negativa.");
            }
            else if (!vistas.Add(fila.Value))
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, $"La fila {fila} ya está en otra línea.");
            }
            else if (filasEnHoja is { } total && fila >= total)
            {
                Linea("fila", clsCodigosLineaSolicitud.FilaInvalida, $"La fila {fila} no existe en la hoja '{entrada.Hoja}' ({total} filas con datos).");
            }
        }

        return errores;
    }
}
