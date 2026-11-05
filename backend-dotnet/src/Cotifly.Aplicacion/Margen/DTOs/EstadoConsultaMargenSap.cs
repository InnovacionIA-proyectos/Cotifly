namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>Resultado de consultar la fuente del margen de SAP TM (<see cref="Interfaces.IClsFuenteMargen"/>, HU-042.2).</summary>
public enum EstadoConsultaMargenSap
{
    /// <summary>
    /// La copia local nunca se sincronizó (ninguna corrida <c>ok</c> de la sincronización del backend): se trata como "SAP TM no
    /// configurado" y el resolver usa la política de la línea. Es el caso de un ambiente local o sin las credenciales de SAP.
    /// </summary>
    NoConfigurada,

    /// <summary>La copia existe pero no tiene una fila activa para (organización, TrqType, BP).</summary>
    SinRegistro,

    /// <summary>Hay margen de SAP TM para el cliente.</summary>
    ConValor,
}
