namespace Cotifly.Dominio.Margen;

/// <summary>
/// De dónde salió el margen aplicado (<c>Cotizaciones.origenMargen</c>):
/// <c>sap_tm</c> (rentabilidad por cliente en SAP TM) | <c>politica_linea</c> (<c>MargenesPolitica</c>) |
/// <c>negociado</c> (ajuste del comercial, queda en <c>MargenAjustes</c>).
/// </summary>
public enum OrigenMargen
{
    SapTm,
    PoliticaLinea,
    Negociado,
}
